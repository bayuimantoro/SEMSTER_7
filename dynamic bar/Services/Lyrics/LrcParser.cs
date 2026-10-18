using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace IslandWin.Services.Lyrics;

/// <summary>
/// Parses LRC format lyrics into a <see cref="SyncedLyrics"/>.
///
/// Supports:
/// - Single timestamp: [01:23.45]Line text
/// - Multiple timestamps per line: [00:01.00][01:23.45]Repeated line
/// - [offset:±ms] metadata tag (applied to all timestamps)
/// - Plain text fallback: when no timestamps are found, the raw text is
///   stored as plain lyrics and HasSynced is false
/// - Multi-line timestamp-less metadata tags (id3 tags, etc.) are ignored
/// </summary>
public static class LrcParser
{
    // Matches one or more [mm:ss.xx] timestamps at the start of a line.
    private static readonly Regex TimestampRegex =
        new(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

    // [offset:+120] or [offset:-120] (milliseconds) or [offset:120]
    private static readonly Regex OffsetRegex =
        new(@"^\[offset:\s*([+-]?\d+)\s*\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

    /// <summary>
    /// Parses LRC text. Returns <see cref="SyncedLyrics.Empty"/> for null/empty
    /// input. Returns a plain-only SyncedLyrics when no timestamps are found.
    /// </summary>
    public static SyncedLyrics Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
            return SyncedLyrics.Empty;

        long offsetMs = 0;
        var lines = lrc.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var timedLines = new List<LyricLine>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            // Check for offset tag first.
            var offsetMatch = OffsetRegex.Match(rawLine.Trim());
            if (offsetMatch.Success)
            {
                if (long.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsedOffset))
                {
                    offsetMs = parsedOffset;
                }
                continue; // offset tag line has no lyric text
            }

            // Find all timestamps at the start of the line.
            var matches = TimestampRegex.Matches(rawLine);
            if (matches.Count == 0)
            {
                // Not a timed line. Could be metadata like [ar:Artist], [ti:Title].
                // Skip lines that look like metadata tags.
                if (rawLine.TrimStart().StartsWith("[") && rawLine.Contains("]"))
                    continue;
                // Otherwise it's plain text — will be captured as plain fallback.
                continue;
            }

            // The text is everything after the last timestamp.
            var text = rawLine.Substring(matches[matches.Count - 1].Index +
                                          matches[matches.Count - 1].Length).Trim();

            // Each timestamp creates a separate LyricLine (supports multi-timestamp).
            foreach (Match ts in matches)
            {
                var seconds = ParseTimestampSeconds(ts);
                if (seconds < 0) continue;

                // Apply offset (convert ms to seconds).
                var adjusted = seconds - (offsetMs / 1000.0);
                if (adjusted < 0) adjusted = 0;

                timedLines.Add(new LyricLine(TimeSpan.FromSeconds(adjusted), text));
            }
        }

        if (timedLines.Count >= 2)
        {
            timedLines.Sort((a, b) => a.Time.CompareTo(b.Time));
            return SyncedLyrics.Synced(timedLines, offsetMs);
        }

        // No usable timestamps → fall back to plain lyrics (strip metadata tags).
        var plain = StripMetadataTags(lrc);
        return string.IsNullOrWhiteSpace(plain)
            ? SyncedLyrics.Empty
            : SyncedLyrics.Plain(plain);
    }

    private static double ParseTimestampSeconds(Match match)
    {
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var minutes)) return -1;

        if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds)) return -1;

        return minutes * 60 + seconds;
    }

    /// <summary>
    /// Strips LRC metadata tags ([ar:], [ti:], [al:], [offset:], [by:], etc.)
    /// and timestamp tags, returning only the lyric text lines.
    /// </summary>
    private static string StripMetadataTags(string lrc)
    {
        var lines = lrc.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.Trim();

            // Skip metadata tag lines like [ar:Artist], [ti:Title], [offset:...]
            if (trimmed.StartsWith("[") && trimmed.Contains(":") && trimmed.Contains("]"))
            {
                // Check if it's a known metadata tag
                var tagEnd = trimmed.IndexOf(']');
                if (tagEnd > 0)
                {
                    var tagContent = trimmed.Substring(1, tagEnd - 1);
                    var colonIdx = tagContent.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var tag = tagContent.Substring(0, colonIdx).ToLowerInvariant();
                        // Known metadata tags — skip the whole line
                        if (tag is "ar" or "ti" or "al" or "by" or "offset" or "length" or "re" or "ve")
                            continue;
                    }
                }
            }

            // Strip timestamps from the line for plain display.
            var stripped = TimestampRegex.Replace(line, "").Trim();
            if (!string.IsNullOrWhiteSpace(stripped))
                result.Add(stripped);
        }
        return string.Join(Environment.NewLine, result);
    }
}
