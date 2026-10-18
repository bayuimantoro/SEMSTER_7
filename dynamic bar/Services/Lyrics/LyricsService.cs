using System;
using System.Threading;
using System.Threading.Tasks;

namespace IslandWin.Services.Lyrics;

/// <summary>
/// Orchestrates lyrics fetching on track changes. Subscribes to
/// <see cref="MediaSessionService.MediaChanged"/>, normalizes the title/artist
/// (conservative YouTube suffix stripping), and fetches lyrics via
/// <see cref="ILyricsProvider"/>. Raises <see cref="LyricsChanged"/> when
/// lyrics arrive.
///
/// Stale-request guard: each fetch is tagged with a generation token. If a
/// new track change arrives before the previous fetch completes, the old
/// fetch's result is discarded. This prevents a slow fetch for track A from
/// overwriting lyrics for track B.
/// </summary>
public sealed class LyricsService : IDisposable
{
    private readonly ILyricsProvider _provider;
    private readonly MediaSessionService _media;
    private long _generation;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Current lyrics (synced or plain), or null when none available.</summary>
    public SyncedLyrics? Current { get; private set; }

    /// <summary>
    /// Raised when lyrics for the current track change. Raised on the GSMTC
    /// thread (non-UI); the caller must marshal to the UI thread.
    /// </summary>
    public event EventHandler<LyricsChangedEventArgs>? LyricsChanged;

    private string _lastFetchedKey = string.Empty;

    public LyricsService(ILyricsProvider provider, MediaSessionService media)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _media.MediaChanged += OnMediaChanged;
    }

    /// <summary>
    /// Call at startup to fetch lyrics for whatever track is already playing.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        if (_media.Current.HasMedia)
        {
            _lastFetchedKey = $"{_media.Current.Title}|{_media.Current.Source}";
            _ = FetchLyricsAsync(_media.Current);
        }
    }

    private void OnMediaChanged(object? sender, MediaSnapshot snap)
    {
        if (_disposed) return;

        var key = $"{snap.Title}|{snap.Source}";
        // If track title and artist are unchanged and we already have lyrics or are fetching,
        // ignore metadata events caused solely by artwork or playback status changes.
        if (key == _lastFetchedKey && Current is not null)
            return;

        _lastFetchedKey = key;
        _ = FetchLyricsAsync(snap);
    }

    private async Task FetchLyricsAsync(MediaSnapshot snap)
    {
        // Cancel any in-flight fetch — stale request guard.
        var localCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _cts, localCts);
        try { oldCts?.Cancel(); } catch { /* ignore */ }
        oldCts?.Dispose();

        var token = localCts.Token;
        var gen = Interlocked.Increment(ref _generation);

        // Immediately clear prior track lyrics so they do not linger during the fetch.
        Current = null;
        LyricsChanged?.Invoke(this, new LyricsChangedEventArgs(null, snap.Title, snap.Source));

        if (!snap.HasMedia)
        {
            localCts.Dispose();
            return;
        }

        var title = TitleNormalizer.Normalize(snap.Title);
        var artist = TitleNormalizer.NormalizeArtist(snap.Source);

        // If normalization produced an empty title, skip fetch.
        if (string.IsNullOrWhiteSpace(title))
        {
            localCts.Dispose();
            return;
        }

        try
        {
            var lyrics = await _provider.FetchAsync(title, artist, string.Empty, 0, token);

            // Stale guard: if generation advanced, drop this result.
            if (Interlocked.Read(ref _generation) != gen) return;
            if (token.IsCancellationRequested) return;

            Current = lyrics;
            LyricsChanged?.Invoke(this, new LyricsChangedEventArgs(lyrics, snap.Title, snap.Source));
        }
        catch (OperationCanceledException)
        {
            // Stale or cancelled — expected, don't raise.
        }
        catch (Exception)
        {
            // Fetch failed — leave current lyrics as-is.
        }
        finally
        {
            // Dispose the local CTS only if it's still the current one.
            if (Interlocked.CompareExchange(ref _cts, null, localCts) == localCts)
            {
                localCts.Dispose();
            }
            else
            {
                // Another fetch took over; the new one owns disposal.
                localCts.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _media.MediaChanged -= OnMediaChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}

/// <summary>Event args for lyrics changes.</summary>
public sealed class LyricsChangedEventArgs : EventArgs
{
    /// <summary>Lyrics for the current track, or null when none available.</summary>
    public SyncedLyrics? Lyrics { get; }

    /// <summary>The raw (un-normalized) track title that lyrics were fetched for.</summary>
    public string Title { get; }

    /// <summary>The source/artist that lyrics were fetched for.</summary>
    public string Source { get; }

    public LyricsChangedEventArgs(SyncedLyrics? lyrics, string title, string source)
    {
        Lyrics = lyrics;
        Title = title;
        Source = source;
    }
}
