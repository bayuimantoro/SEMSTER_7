using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandWin.Services.Lyrics;

/// <summary>
/// LRCLIB lyrics provider. Uses the public API at https://lrclib.net.
///
/// Flow:
/// 1. Try GET /api/get (best match, prefers synced). Validate response title/
///    artist/duration against query where practical.
/// 2. If /api/get misses, try GET /api/search and pick the best result by
///    validating title/artist/duration. Do NOT blindly select the first result.
/// 3. Prefer synced lyrics; fall back to plain lyrics.
/// 4. Cache hits and misses. Misses get a 10-minute TTL (negative cache) so we
///    don't hammer LRCLIB for tracks with no lyrics.
///
/// All network work is async; never blocks the UI thread.
/// </summary>
public sealed class LrclibLyricsProvider : ILyricsProvider, IDisposable
{
    private const string BaseUrl = "https://lrclib.net";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    // Negative cache TTL: don't re-fetch misses for 10 minutes.
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private bool _disposed;

    // Sentinel for negative cache (no lyrics found).
    private static readonly SyncedLyrics NegativeSentinel = SyncedLyrics.Empty;

    public LrclibLyricsProvider()
    {
        _client = new HttpClient { Timeout = Timeout };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "IslandWin/1.0 (https://github.com/islandwin)");
    }

    public async Task<SyncedLyrics?> FetchAsync(
        string title,
        string artist,
        string album,
        long durationMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        string cacheKey = BuildCacheKey(title, artist, album, durationMs);

        // Check cache (positive or negative).
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            if (!entry.IsNegative || DateTime.UtcNow < entry.ExpiresAt)
                return entry.Lyrics;
            // Negative entry expired — remove and re-fetch.
            _cache.TryRemove(cacheKey, out _);
        }

        try
        {
            // Try /api/get first.
            var result = await TryGetAsync(title, artist, album, durationMs, cancellationToken);
            if (result is not null)
            {
                _cache[cacheKey] = CacheEntry.Positive(result);
                return result;
            }

            // Fall back to /api/search with validation.
            result = await TrySearchAsync(title, artist, durationMs, cancellationToken);
            if (result is not null)
            {
                _cache[cacheKey] = CacheEntry.Positive(result);
                return result;
            }

            // No lyrics found — negative cache with TTL.
            _cache[cacheKey] = CacheEntry.Negative(DateTime.UtcNow + NegativeTtl);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout, not cancellation — don't cache, let caller retry later.
            return null;
        }
        catch (Exception)
        {
            // Network/parse error — don't cache, let caller retry later.
            return null;
        }
    }

    private async Task<SyncedLyrics?> TryGetAsync(
        string title, string artist, string album, long durationMs,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/get?track_name={WebUtility.UrlEncode(title)}";
        if (!string.IsNullOrWhiteSpace(artist))
            url += $"&artist_name={WebUtility.UrlEncode(artist)}";
        if (!string.IsNullOrWhiteSpace(album))
            url += $"&album_name={WebUtility.UrlEncode(album)}";
        if (durationMs > 0)
            url += $"&duration={durationMs / 1000.0}";

        using var resp = await _client.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        var item = ParseTrackItem(json);
        if (item is null) return null;

        if (!ValidateResult(item, title, artist, durationMs)) return null;

        return BuildLyrics(item);
    }

    private async Task<SyncedLyrics?> TrySearchAsync(
        string title, string artist, long durationMs,
        CancellationToken ct)
    {
        var url = $"{BaseUrl}/api/search?track_name={WebUtility.UrlEncode(title)}";
        if (!string.IsNullOrWhiteSpace(artist))
            url += $"&artist_name={WebUtility.UrlEncode(artist)}";

        using var resp = await _client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        var items = ParseSearchResults(json);
        if (items is null || items.Count == 0) return null;

        // Pick the best match — don't blindly select the first result.
        // Score by title match, artist match, and duration proximity.
        TrackItem? best = null;
        double bestScore = -1;
        foreach (var item in items)
        {
            double score = ScoreResult(item, title, artist, durationMs);
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        if (best is null || bestScore < 0.5) return null; // too poor a match

        return BuildLyrics(best);
    }

    /// <summary>
    /// Validates a /api/get result against the query. Returns false if the
    /// result title/artist/duration is too far from what we asked for.
    /// </summary>
    private static bool ValidateResult(TrackItem item, string title, string artist, long durationMs)
    {
        // Title must be a reasonable match (normalized substring or similarity).
        if (!TitlesMatch(item.TrackName, title)) return false;

        // If we have an artist query, validate it.
        if (!string.IsNullOrWhiteSpace(artist) &&
            !string.IsNullOrWhiteSpace(item.ArtistName))
        {
            if (!ArtistsMatch(item.ArtistName, artist)) return false;
        }

        // Duration validation: reject if more than 10 seconds off.
        if (durationMs > 0 && item.Duration > 0)
        {
            var diff = Math.Abs(item.Duration - durationMs);
            if (diff > 10_000) return false; // 10s tolerance in ms
        }

        return true;
    }

    private static double ScoreResult(TrackItem item, string title, string artist, long durationMs)
    {
        double score = 0;

        // Title match (weight 0.5)
        if (TitlesMatch(item.TrackName, title)) score += 0.5;

        // Artist match (weight 0.3)
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(item.ArtistName))
        {
            if (ArtistsMatch(item.ArtistName, artist)) score += 0.3;
        }
        else if (string.IsNullOrWhiteSpace(artist))
        {
            // No artist query — don't penalize.
            score += 0.15;
        }

        // Duration proximity (weight 0.2)
        if (durationMs > 0 && item.Duration > 0)
        {
            var diffSec = Math.Abs(item.Duration - durationMs) / 1000.0;
            if (diffSec <= 2) score += 0.2;
            else if (diffSec <= 5) score += 0.15;
            else if (diffSec <= 10) score += 0.1;
        }
        else
        {
            score += 0.1; // no duration to compare
        }

        // Prefer synced lyrics.
        if (!string.IsNullOrWhiteSpace(item.SyncedLyrics)) score += 0.1;

        return score;
    }

    private static bool TitlesMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var na = NormalizeForCompare(a);
        var nb = NormalizeForCompare(b);
        if (na == nb) return true;
        if (na.Contains(nb, StringComparison.OrdinalIgnoreCase) ||
            nb.Contains(na, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ArtistsMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var na = NormalizeForCompare(a);
        var nb = NormalizeForCompare(b);
        return na == nb ||
               na.Contains(nb, StringComparison.OrdinalIgnoreCase) ||
               nb.Contains(na, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForCompare(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        // Lowercase, trim, collapse whitespace, strip punctuation.
        var result = s.Trim().ToLowerInvariant();
        // Remove common punctuation that varies between sources.
        foreach (var c in ".,!?;:'\"()[]{}-_/\\")
            result = result.Replace(c.ToString(), string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
    }

    private static SyncedLyrics? BuildLyrics(TrackItem item)
    {
        // Prefer synced lyrics.
        if (!string.IsNullOrWhiteSpace(item.SyncedLyrics))
        {
            var parsed = LrcParser.Parse(item.SyncedLyrics);
            if (parsed.HasSynced) return parsed;
            // If parsing didn't yield synced lines but we had synced text,
            // fall through to plain.
        }

        if (!string.IsNullOrWhiteSpace(item.PlainLyrics))
        {
            return SyncedLyrics.Plain(item.PlainLyrics);
        }

        return null;
    }

    private static string BuildCacheKey(string title, string artist, string album, long durationMs)
    {
        return $"{NormalizeForCompare(title)}|{NormalizeForCompare(artist)}|{NormalizeForCompare(album)}|{durationMs}";
    }

    // JSON parsing — minimal, using System.Text.Json (built-in, no dependency).
    private sealed record TrackItem(
        int Id,
        string TrackName,
        string ArtistName,
        string AlbumName,
        long Duration,
        string SyncedLyrics,
        string PlainLyrics);

    private static TrackItem? ParseTrackItem(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return MapTrackItem(doc.RootElement);
        }
        catch { return null; }
    }

    private static System.Collections.Generic.List<TrackItem>? ParseSearchResults(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new System.Collections.Generic.List<TrackItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var item = MapTrackItem(el);
                if (item is not null) list.Add(item);
            }
            return list;
        }
        catch { return null; }
    }

    private static TrackItem? MapTrackItem(JsonElement el)
    {
        try
        {
            return new TrackItem(
                Id: el.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                TrackName: el.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "",
                ArtistName: el.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "",
                AlbumName: el.TryGetProperty("albumName", out var al) ? al.GetString() ?? "" : "",
                Duration: el.TryGetProperty("duration", out var d) ? (long)(d.GetDouble() * 1000) : 0,
                SyncedLyrics: el.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() ?? "" : "",
                PlainLyrics: el.TryGetProperty("plainLyrics", out var pl) ? pl.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    private readonly record struct CacheEntry(SyncedLyrics? Lyrics, DateTime ExpiresAt, bool IsNegative)
    {
        public static CacheEntry Positive(SyncedLyrics lyrics) =>
            new(lyrics, DateTime.MaxValue, false);
        public static CacheEntry Negative(DateTime expiresAt) =>
            new(NegativeSentinel, expiresAt, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
