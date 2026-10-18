using System;

namespace IslandWin.Services.Lyrics;

/// <summary>
/// A single line of lyrics with its start time relative to playback start.
/// </summary>
public readonly record struct LyricLine(TimeSpan Time, string Text)
{
    public override string ToString() => $"[{Time.TotalSeconds:F2}] {Text}";
}
