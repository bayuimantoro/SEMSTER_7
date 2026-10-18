using System;
using System.Text.RegularExpressions;

namespace IslandWin.Services.Lyrics;

/// <summary>
/// Conservative title normalization for YouTube and other browser-based players.
/// Removes common YouTube suffixes and cleanup tokens. Does NOT parse the
/// artist from the title (too fragile) — just strips noise so LRCLIB's fuzzy
/// match has a cleaner query.
/// </summary>
public static class TitleNormalizer
{
    // Patterns are applied in order; case-insensitive.
    // These are conservative: only strip tokens that are clearly not part of
    // the actual song title.
    private static readonly (Regex, string)[] YouTubeSuffixPatterns =
    {
        // "(Official Video)", "[Official Video]", "(Official Music Video)"
        (new(@"[\(\[]official\s+(music\s+)?video[\)\]]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), ""),
        // "(Official Audio)", "[Official Audio]"
        (new(@"[\(\[]official\s+audio[\)\]]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), ""),
        // "(Lyrics)", "[Lyrics]", "(Lyric Video)", "[Lyric Video]"
        (new(@"[\(\[]lyric(s)?\s*(video)?[\)\]]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), ""),
        // "[MV]", "(MV)"
        (new(@"[\(\[]MV[\)\]]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), ""),
        // "▶ " prefix marker some browser players add
        (new(@"^\s*▶\s*", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), ""),
        // " | YouTube" suffix
        (new(@"\s*\|\s*YouTube\s*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), ""),
        // "- YouTube" suffix
        (new(@"\s*-\s*YouTube\s*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), ""),
    };

    /// <summary>
    /// Normalizes a title for querying. Removes common YouTube noise suffixes
    /// and collapses whitespace. Does not lower-case (LRCLIB does its own
    /// normalization). Returns empty string if input is null/whitespace.
    /// </summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var result = title.Trim();
        foreach (var (pattern, replacement) in YouTubeSuffixPatterns)
        {
            result = pattern.Replace(result, replacement);
        }

        // Collapse multiple whitespace to single space.
        result = Regex.Replace(result, @"\s+", " ").Trim();

        return result;
    }

    /// <summary>
    /// Normalizes an artist name. Browser-based players often leave artist
    /// blank; this just trims and collapses whitespace.
    /// </summary>
    public static string NormalizeArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        return Regex.Replace(artist.Trim(), @"\s+", " ").Trim();
    }
}
