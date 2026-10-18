using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace IslandWin.Services;

/// <summary>
/// Wraps the GlobalSystemMediaTransportControlsSessionManager (GSMTC) and exposes
/// the currently active media session to the UI. Stage 2 phase 2: detection +
/// metadata display + media controls (Previous/PlayPause/Next).
///
/// Delay strategy: metadata (title/artist/status/controls) is raised to the UI
/// immediately after <see cref="GlobalSystemMediaTransportControlsSession.TryGetMediaPropertiesAsync"/>
/// returns. Artwork is loaded afterwards on a background path and tagged with a
/// generation token so a stale thumbnail can never overwrite a newer snapshot
/// (the classic "previous song's art lingers" race).
///
/// Thread-safety: GSMTC events fire on a non-UI thread. The UI thread
/// marshalling is the caller's responsibility.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _disposed;

    // Timeline tracking: Stopwatch-based playback position estimator anchored to GSMTC.
    // GSMTC Position is only a snapshot as of LastUpdatedTime and is NOT continuously updated
    // by apps like Spotify during playback. A running Stopwatch bridges the gaps between
    // GSMTC timeline events with microsecond accuracy.
    private readonly object _timelineLock = new();
    private TimeSpan _anchorPosition = TimeSpan.Zero;
    private TimeSpan _anchorEndTime = TimeSpan.Zero;
    private readonly Stopwatch _positionStopwatch = new();
    private bool _isPlaying;

    // Cached snapshot so callers can read without awaiting async APIs.
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;

    /// <summary>
    /// Monotonic counter incremented on every session switch and every
    /// metadata refresh. An in-flight artwork load captures the value at
    /// start; if it doesn't match when it completes, the result is discarded.
    /// </summary>
    private long _generation;

    /// <summary>Current snapshot of the active session's metadata. Thread-safe read.</summary>
    public MediaSnapshot Current => _snapshot;

    /// <summary>Raised on the GSMTC thread when title/artist/artwork/source changes.</summary>
    public event EventHandler<MediaSnapshot>? MediaChanged;

    /// <summary>Raised on the GSMTC thread when the playback status (playing/paused/...) changes.</summary>
    public event EventHandler<PlaybackStatus>? PlaybackStatusChanged;

    /// <summary>
    /// Raised on the GSMTC thread when the timeline changes (seek, position
    /// jump, or track start/end times change). Used by lyrics sync to
    /// recompute the active line immediately after a seek.
    /// </summary>
    public event EventHandler? TimelineChanged;

    /// <summary>
    /// Current estimated playback position, or TimeSpan.Zero when no timeline data is
    /// available. Maintains real-time accuracy via Stopwatch while playing and re-anchors
    /// whenever fresh GSMTC timeline/playback events arrive. Safe to call from any thread.
    /// </summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            lock (_timelineLock)
            {
                var session = _currentSession;
                if (session is null) return TimeSpan.Zero;

                TimeSpan pos = _anchorPosition;
                if (_isPlaying && _positionStopwatch.IsRunning)
                {
                    pos += _positionStopwatch.Elapsed;
                }

                if (_anchorEndTime > TimeSpan.Zero && pos > _anchorEndTime)
                {
                    pos = _anchorEndTime;
                }
                if (pos < TimeSpan.Zero)
                {
                    pos = TimeSpan.Zero;
                }

                return pos;
            }
        }
    }

    /// <summary>
    /// Current track end time, or TimeSpan.Zero when no timeline data is
    /// available.
    /// </summary>
    public TimeSpan CurrentEndTime
    {
        get
        {
            lock (_timelineLock)
            {
                return _anchorEndTime;
            }
        }
    }

    /// <summary>
    /// Re-anchors the Stopwatch-based timeline estimator against the current GSMTC state.
    /// Compensates for latency between the session's LastUpdatedTime and UtcNow.
    /// </summary>
    private void ReanchorTimeline(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            lock (_timelineLock)
            {
                _anchorPosition = TimeSpan.Zero;
                _anchorEndTime = TimeSpan.Zero;
                _positionStopwatch.Reset();
                _isPlaying = false;
            }
            return;
        }

        try
        {
            var timeline = session.GetTimelineProperties();
            var info = session.GetPlaybackInfo();
            var status = MapStatus(info);
            bool isPlaying = status == PlaybackStatus.Playing;

            lock (_timelineLock)
            {
                _isPlaying = isPlaying;
                _anchorEndTime = timeline.EndTime;

                var basePos = timeline.Position;
                var lastUpdated = timeline.LastUpdatedTime;

                // GSMTC Position is recorded as of LastUpdatedTime.
                // When actively playing, calculate elapsed time since LastUpdatedTime
                // so we don't start from a stale position if the update was generated
                // in the past.
                if (isPlaying && lastUpdated > DateTimeOffset.MinValue)
                {
                    var elapsedSinceUpdate = DateTimeOffset.UtcNow - lastUpdated.ToUniversalTime();
                    if (elapsedSinceUpdate > TimeSpan.Zero && elapsedSinceUpdate < TimeSpan.FromSeconds(30))
                    {
                        basePos += elapsedSinceUpdate;
                    }
                }

                _anchorPosition = basePos;

                if (isPlaying)
                {
                    _positionStopwatch.Restart();
                }
                else
                {
                    _positionStopwatch.Reset();
                }
            }
        }
        catch (Exception)
        {
            // Session can throw if closing/disconnecting.
        }
    }

    /// <summary>
    /// Connects to the GSMTC manager and subscribes to session events.
    /// Safe to call once at startup. Returns false if GSMTC is unavailable
    /// (e.g. running as SYSTEM, or on an older Windows build).
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_disposed) return false;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager is null) return false;

            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;

            // Pick up whatever is active right now.
            OnCurrentSessionChanged(_manager, null);
            return true;
        }
        catch (Exception)
        {
            // GSMTC access can fail in restricted contexts; fail soft.
            _manager = null;
            return false;
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        // The current session reference may have become stale; refresh it.
        OnCurrentSessionChanged(sender, null);
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object? args)
    {
        var newSession = sender.GetCurrentSession();
        SwitchSession(newSession);
    }

    private void SwitchSession(GlobalSystemMediaTransportControlsSession? newSession)
    {
        // Detach from the old session.
        if (_currentSession is not null)
        {
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _currentSession = newSession;

        // Bump generation so any in-flight artwork load from the previous
        // session is discarded when it completes.
        Interlocked.Increment(ref _generation);

        if (newSession is null)
        {
            ReanchorTimeline(null);
            UpdateSnapshot(MediaSnapshot.Empty, statusChanged: true);
            return;
        }

        newSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
        newSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        newSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

        ReanchorTimeline(newSession);
        // Read the initial state synchronously where possible; artwork is async.
        _ = RefreshAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        try
        {
            var info = sender.GetPlaybackInfo();
            var status = MapStatus(info);
            var controls = MapControls(info);
            _snapshot = _snapshot.WithPlayback(status, controls);
            ReanchorTimeline(sender);
            // Raise both events: status always changes, controls often do too.
            MediaChanged?.Invoke(this, _snapshot);
            PlaybackStatusChanged?.Invoke(this, status);
        }
        catch (Exception)
        {
            // Surface can throw if the session is gone; ignore.
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshAsync();
    }

    /// <summary>
    /// Raised by GSMTC when the timeline changes (seek, position jump, or
    /// track start/end times change). Forwards to <see cref="TimelineChanged"/>
    /// so lyrics sync can recompute the active line immediately after a seek.
    /// </summary>
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            ReanchorTimeline(sender);
            TimelineChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* session gone; ignore */ }
    }

    /// <summary>
    /// Re-reads metadata + status from the current session and raises the
    /// appropriate events. Metadata is pushed to the UI first; artwork is
    /// loaded afterwards and tagged with the current generation so a stale
    /// thumbnail can never overwrite a newer snapshot.
    /// </summary>
    private async Task RefreshAsync()
    {
        var session = _currentSession;
        if (session is null)
        {
            UpdateSnapshot(MediaSnapshot.Empty, statusChanged: true);
            return;
        }

        // Capture the generation at the start of this refresh. Any artwork
        // produced by a refresh whose generation no longer matches is dropped.
        long gen = Interlocked.Increment(ref _generation);

        try
        {
            // Phase 1 — metadata + status + controls (fast path to UI).
            var propsTask = session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            var status = MapStatus(info);
            var controls = MapControls(info);

            var props = await propsTask;

            var title = props?.Title ?? string.Empty;
            var artist = props?.Artist ?? string.Empty;
            // Fall back to the source app id when the artist field is blank
            // (common for browser-based players like Edge/YouTube).
            var source = string.IsNullOrWhiteSpace(artist)
                ? TryGetSourceAppId(session)
                : artist;

            // Push text + status + controls immediately, without waiting on
            // artwork. This is the key latency fix.
            var snap = new MediaSnapshot(title, source, status, _snapshot.Artwork, controls);
            ReanchorTimeline(session);
            UpdateSnapshot(snap, statusChanged: true);

            // Phase 2 — artwork (slow path, generation-guarded).
            if (props?.Thumbnail is not null)
            {
                _ = LoadArtworkAsync(props.Thumbnail, gen, title, source, status, controls);
            }
        }
        catch (Exception)
        {
            // Session may have died mid-read; leave the existing snapshot.
        }
    }

    /// <summary>
    /// Loads the thumbnail off the refresh path and applies it only if the
    /// generation still matches. If a newer refresh has started (song changed,
    /// session switched), the result is discarded so a stale thumbnail can
    /// never overwrite the current snapshot.
    /// </summary>
    private async Task LoadArtworkAsync(
        Windows.Storage.Streams.IRandomAccessStreamReference thumbnail,
        long gen,
        string title,
        string source,
        PlaybackStatus status,
        MediaControlsSupported controls)
    {
        ImageSource? art;
        try
        {
            art = await LoadThumbnailAsync(thumbnail);
        }
        catch (Exception)
        {
            return;
        }

        // Race guard: if the generation advanced, drop this artwork.
        if (Interlocked.Read(ref _generation) != gen) return;

        // Only apply if the snapshot still describes the same track. This
        // protects against the (unlikely) case where the generation check
        // passed but the in-memory snapshot was replaced by a different
        // track in the meantime.
        var current = _snapshot;
        if (current.Title != title || current.Source != source)
        {
            // Track changed after we started loading; the newer refresh owns
            // the snapshot now. Drop our artwork.
            return;
        }

        var updated = current.WithArtwork(art);
        _snapshot = updated;
        MediaChanged?.Invoke(this, updated);
    }

    private void UpdateSnapshot(MediaSnapshot snap, bool statusChanged)
    {
        _snapshot = snap;
        MediaChanged?.Invoke(this, snap);
        if (statusChanged)
            PlaybackStatusChanged?.Invoke(this, snap.Status);
    }

    /// <summary>
    /// Toggles play/pause on the current session. Picks the right call based
    /// on the current playback status and which controls the session reports
    /// as enabled. Returns false if there is no session or the operation
    /// failed/was rejected by the source app.
    /// </summary>
    public async Task<bool> TryPlayPauseAsync()
    {
        var session = _currentSession;
        if (session is null) return false;

        try
        {
            var info = session.GetPlaybackInfo();
            var controls = info?.Controls;
            var status = MapStatus(info);

            // Prefer the semantically correct call: pause if playing, play if paused.
            if (status == PlaybackStatus.Playing && controls?.IsPauseEnabled == true)
                return await session.TryPauseAsync();
            if (status != PlaybackStatus.Playing && controls?.IsPlayEnabled == true)
                return await session.TryPlayAsync();

            // Some sources expose a dedicated toggle but not play/pause separately.
            if (controls?.IsPlayPauseToggleEnabled == true)
            {
                // No dedicated toggle method on GSMTC; fall back to play which
                // most sources interpret as a toggle when already playing.
                return await session.TryPlayAsync();
            }

            // Last resort: try the call the controls suggest is enabled, even
            // if the status check above didn't match (some apps report oddly).
            if (controls?.IsPlayEnabled == true) return await session.TryPlayAsync();
            if (controls?.IsPauseEnabled == true) return await session.TryPauseAsync();
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sends a skip-next command to the current session.</summary>
    public async Task<bool> TrySkipNextAsync()
    {
        var session = _currentSession;
        if (session is null) return false;
        try
        {
            var controls = session.GetPlaybackInfo()?.Controls;
            if (controls?.IsNextEnabled != true) return false;
            return await session.TrySkipNextAsync();
        }
        catch (Exception) { return false; }
    }

    /// <summary>Sends a skip-previous command to the current session.</summary>
    public async Task<bool> TrySkipPreviousAsync()
    {
        var session = _currentSession;
        if (session is null) return false;
        try
        {
            var controls = session.GetPlaybackInfo()?.Controls;
            if (controls?.IsPreviousEnabled != true) return false;
            return await session.TrySkipPreviousAsync();
        }
        catch (Exception) { return false; }
    }

    /// <summary>Requests the source application to seek to the specified position.</summary>
    public async Task<bool> TryChangePlaybackPositionAsync(TimeSpan position)
    {
        var session = _currentSession;
        if (session is null) return false;
        try
        {
            var controls = session.GetPlaybackInfo()?.Controls;
            if (controls?.IsPlaybackPositionEnabled != true) return false;
            return await session.TryChangePlaybackPositionAsync((long)position.TotalMilliseconds * 10_000);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Converts the WinRT IRandomAccessStreamReference thumbnail into a WPF
    /// ImageSource. Returns null on failure or when no thumbnail is available.
    /// Uses DataReader.FromBuffer to avoid the System.Runtime.WindowsRuntime
    /// extension methods not present in the .NET 6 built-in WinRT projection.
    /// </summary>
    private static async Task<ImageSource?> LoadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var winrtStream = await thumbnail.OpenReadAsync();
            var size = (int)winrtStream.Size;
            var buffer = new Windows.Storage.Streams.Buffer((uint)size);
            await winrtStream.ReadAsync(buffer, (uint)size, Windows.Storage.Streams.InputStreamOptions.None);

            var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
            var bytes = new byte[buffer.Length];
            reader.ReadBytes(bytes);
            var netStream = new System.IO.MemoryStream(bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // read+close immediately
            bmp.StreamSource = netStream;
            bmp.EndInit();
            bmp.Freeze(); // make it cross-thread accessible
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TryGetSourceAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.SourceAppUserModelId ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static PlaybackStatus MapStatus(GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
    {
        if (info is null) return PlaybackStatus.Unknown;
        return info.PlaybackStatus switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackStatus.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackStatus.Closed,
            _ => PlaybackStatus.Unknown,
        };
    }

    private static MediaControlsSupported MapControls(GlobalSystemMediaTransportControlsSessionPlaybackInfo? info)
    {
        if (info is null) return MediaControlsSupported.None;
        var c = info.Controls;
        if (c is null) return MediaControlsSupported.None;
        return new MediaControlsSupported(
            IsPlayPauseEnabled: c.IsPlayEnabled || c.IsPauseEnabled || c.IsPlayPauseToggleEnabled,
            IsNextEnabled: c.IsNextEnabled,
            IsPreviousEnabled: c.IsPreviousEnabled);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_timelineLock)
        {
            _positionStopwatch.Reset();
            _isPlaying = false;
        }

        if (_currentSession is not null)
        {
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            _currentSession = null;
        }

        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
    }
}

/// <summary>Immutable snapshot of the active session's current metadata.</summary>
public sealed record MediaSnapshot(
    string Title,
    string Source, // artist, or source app id if artist is blank
    PlaybackStatus Status,
    ImageSource? Artwork,
    MediaControlsSupported Controls)
{
    public static MediaSnapshot Empty { get; } = new(
        string.Empty, string.Empty, PlaybackStatus.Unknown, null, MediaControlsSupported.None);

    public bool HasMedia => !string.IsNullOrWhiteSpace(Title);

    public MediaSnapshot WithStatus(PlaybackStatus status) =>
        this with { Status = status };

    public MediaSnapshot WithPlayback(PlaybackStatus status, MediaControlsSupported controls) =>
        this with { Status = status, Controls = controls };

    public MediaSnapshot WithArtwork(ImageSource? art) =>
        this with { Artwork = art };
}

/// <summary>Which media control buttons the current session supports.</summary>
public readonly record struct MediaControlsSupported(
    bool IsPlayPauseEnabled,
    bool IsNextEnabled,
    bool IsPreviousEnabled)
{
    public static MediaControlsSupported None => default;
}

public enum PlaybackStatus
{
    Unknown,
    Playing,
    Paused,
    Stopped,
    Changing,
    Closed,
}
