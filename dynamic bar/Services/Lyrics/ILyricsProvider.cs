using System.Threading;
using System.Threading.Tasks;

namespace IslandWin.Services.Lyrics;

/// <summary>
/// Abstraction for a lyrics provider. Implementations fetch lyrics for a track
/// and return a <see cref="SyncedLyrics"/> (synced preferred, plain fallback),
/// or null when no lyrics are available.
/// </summary>
public interface ILyricsProvider
{
    /// <summary>
    /// Fetches lyrics. Returns null when the provider has no lyrics for the
    /// given track. Implementations should cache results (including negative
    /// cachesets with a TTL) to avoid re-fetching misses.
    /// </summary>
    /// <param name="title">Track title (already normalized by caller).</param>
    /// <param name="artist">Track artist (already normalized). May be empty.</param>
    /// <param name="album">Track album. May be empty.</param>
    /// <param name="durationMs">Track duration in milliseconds, 0 when unknown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SyncedLyrics?> FetchAsync(
        string title,
        string artist,
        string album,
        long durationMs,
        CancellationToken cancellationToken);
}
