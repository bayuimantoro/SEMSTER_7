using System;
using System.Collections.Generic;

namespace IslandWin.Services.Lyrics;

/// <summary>
/// Holds parsed lyrics: either a timed sequence of lines (synced), a block of
/// plain text (unsynced), or neither (no lyrics found).
///
/// Plain lyrics are NEVER presented as synced. <see cref="HasSynced"/> is true
/// only when at least two timestamped lines were parsed. A single timestamped
/// line could be metadata noise, so we require two or more.
/// </summary>
public sealed class SyncedLyrics
{
    private readonly List<LyricLine> _lines;

    private SyncedLyrics(List<LyricLine> lines, string plain, long offsetMs)
    {
        _lines = lines;
        PlainLyrics = plain;
        OffsetMs = offsetMs;
    }

    /// <summary>
    /// Offset in milliseconds applied to all line timestamps. The parser adds
    /// this to each timestamp. A negative offset shifts lines earlier.
    /// </summary>
    public long OffsetMs { get; }

    /// <summary>Plain (unsynced) lyrics text. Empty when only synced lines exist.</summary>
    public string PlainLyrics { get; }

    /// <summary>True when at least two timestamped lines were parsed. Never true for plain-only.</summary>
    public bool HasSynced => _lines.Count >= 2;

    /// <summary>True when plain lyrics text is non-empty but no timestamps exist.</summary>
    public bool HasPlainOnly => _lines.Count == 0 && !string.IsNullOrWhiteSpace(PlainLyrics);

    /// <summary>Read-only view of the timed lines, ordered by Time ascending.</summary>
    public IReadOnlyList<LyricLine> Lines => _lines;

    /// <summary>
    /// Binary search to find the active line index for a given playback position.
    /// Returns -1 when position is before the first line.
    /// </summary>
    public int FindActiveIndex(TimeSpan position)
    {
        if (_lines.Count == 0) return -1;

        var pos = position.TotalSeconds;
        int lo = 0, hi = _lines.Count - 1;
        if (pos < _lines[0].Time.TotalSeconds) return -1;

        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (_lines[mid].Time.TotalSeconds <= pos)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    public static SyncedLyrics Empty { get; } =
        new(new List<LyricLine>(), string.Empty, 0);

    public static SyncedLyrics Synced(List<LyricLine> lines, long offsetMs) =>
        new(lines ?? throw new ArgumentNullException(nameof(lines)), string.Empty, offsetMs);

    public static SyncedLyrics Plain(string plain) =>
        new(new List<LyricLine>(), plain ?? string.Empty, 0);
}
