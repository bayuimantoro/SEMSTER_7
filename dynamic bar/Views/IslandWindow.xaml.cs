using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandWin.Services;
using IslandWin.Services.Lyrics;

namespace IslandWin.Views;

public partial class IslandWindow : Window
{
    private bool _isExpanded;
    private readonly DispatcherTimer _clockTimer;
    private readonly MediaSessionService _media;
    private readonly LrclibLyricsProvider _lyricsProvider;
    private readonly LyricsService _lyrics;
    private readonly DispatcherTimer _lyricsTimer;
    private SyncedLyrics? _currentLyrics;
    private int _lastActiveLyricIndex = -2;

    // Dimensions & Animation parameters
    private const double CompactWidth = 220;
    private const double CompactHeight = 40;
    private const double ExpandedWidth = 480;
    private const double ExpandedHeight = 118;
    private const double VerticalSlideDistance = 10.0;
    private static readonly Duration TransitionDuration = new(TimeSpan.FromMilliseconds(550));
    // GeometryEase: Smooth Quartic EaseInOut for pill morphing and position, giving a deliberate start and gentle finish.
    private static readonly IEasingFunction GeometryEase = new QuarticEase { EasingMode = EasingMode.EaseInOut };
    // ContentEase: Quartic EaseOut for content fading and parallax slide to ensure smooth, organic settle.
    private static readonly IEasingFunction ContentEase = new QuarticEase { EasingMode = EasingMode.EaseOut };

    private int _transitionCounter;

    public IslandWindow()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm");
        _clockTimer.Start();
        ClockText.Text = DateTime.Now.ToString("HH:mm");

        // Stage 2 phase 1: media session detection + metadata display.
        _media = new MediaSessionService();
        _media.MediaChanged += OnMediaChanged;
        _media.PlaybackStatusChanged += OnPlaybackStatusChanged;
        _media.TimelineChanged += OnTimelineChanged;
        _ = _media.StartAsync(); // fire-and-forget; errors are handled inside the service

        // Stage 4: synced lyrics via LRCLIB.
        _lyricsProvider = new LrclibLyricsProvider();
        _lyrics = new LyricsService(_lyricsProvider, _media);
        _lyrics.LyricsChanged += OnLyricsChanged;
        _lyrics.Start();

        // 100ms gated timer — only runs when playing AND synced lyrics exist.
        // Fast enough to track lyric transitions without perceptible human lag (~100ms),
        // while skipping UI updates unless the active lyric index actually changes.
        _lyricsTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _lyricsTimer.Tick += (_, _) =>
        {
            UpdateActiveLyric();
            UpdatePlaybackProgress();
        };
    }

    /// <summary>
    /// Marshals the GSMTC metadata update to the UI thread and refreshes the
    /// expanded panel. Safe to call when the session is empty (shows fallback).
    /// </summary>
    private void OnMediaChanged(object? sender, MediaSnapshot snap)
    {
        Dispatcher.BeginInvoke(new Action(() => ApplyMediaSnapshot(snap)),
            DispatcherPriority.DataBind);
    }

    private void OnPlaybackStatusChanged(object? sender, PlaybackStatus status)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyStatus(status);
            UpdateTimerStates(status);
            UpdatePlaybackProgress();
        }), DispatcherPriority.DataBind);
    }

    /// <summary>
    /// Timeline changed (seek or track duration change). Recompute the active lyric line and progress immediately.
    /// </summary>
    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastActiveLyricIndex = -2;
            UpdateActiveLyric();
            UpdatePlaybackProgress();
        }), DispatcherPriority.DataBind);
    }

    /// <summary>
    /// Lyrics arrived (or were cleared) from the LyricsService. Marshal to UI
    /// thread and apply.
    /// </summary>
    private void OnLyricsChanged(object? sender, LyricsChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => ApplyLyrics(e.Lyrics)),
            DispatcherPriority.DataBind);
    }

    /// <summary>
    /// Applies the current lyrics to the UI. Synced lyrics show the active +
    /// next line panels; plain lyrics show the first few lines as static text;
    /// null lyrics display an explicit unavailable state.
    /// </summary>
    private void ApplyLyrics(SyncedLyrics? lyrics)
    {
        _currentLyrics = lyrics;
        _lastActiveLyricIndex = -2;

        if (lyrics is null || (!lyrics.HasSynced && !lyrics.HasPlainOnly))
        {
            ActiveLyricText.Text = _media.Current.HasMedia ? "Lyrics unavailable" : "";
            NextLyricText.Text = "";
            _lyricsTimer.Stop();
            return;
        }

        if (lyrics.HasSynced)
        {
            // Synced: let the 100ms timer drive active/next lines.
            UpdateActiveLyric();
            UpdateTimerStates(_media.Current.Status);
        }
        else if (lyrics.HasPlainOnly)
        {
            // Plain: show static text (timer still runs if media playing for progress bar).
            ActiveLyricText.Text = TruncatePlain(lyrics.PlainLyrics, 2);
            NextLyricText.Text = "";
            UpdateTimerStates(_media.Current.Status);
        }
    }

    /// <summary>
    /// Reads GSMTC playback position (real-time Stopwatch-estimated) and updates
    /// the active and next lyric lines. Skips work if index has not changed.
    /// </summary>
    private void UpdateActiveLyric()
    {
        if (_currentLyrics is null || !_currentLyrics.HasSynced) return;
        if (!_isExpanded) return;

        var position = _media.CurrentPosition;
        int idx = _currentLyrics.FindActiveIndex(position);

        if (idx == _lastActiveLyricIndex)
            return; // Unchanged index, zero UI layout/rendering work

        _lastActiveLyricIndex = idx;

        if (idx < 0)
        {
            // Before the first line — show musical note and upcoming first line.
            ActiveLyricText.Text = "♫";
            NextLyricText.Text = _currentLyrics.Lines.Count > 0 ? _currentLyrics.Lines[0].Text : "";
            return;
        }

        var currentText = _currentLyrics.Lines[idx].Text;
        ActiveLyricText.Text = string.IsNullOrWhiteSpace(currentText) ? "♫" : currentText;

        // Show the next line as upcoming.
        if (idx + 1 < _currentLyrics.Lines.Count)
        {
            var nextText = _currentLyrics.Lines[idx + 1].Text;
            NextLyricText.Text = string.IsNullOrWhiteSpace(nextText) ? "♫" : nextText;
        }
        else
        {
            NextLyricText.Text = "";
        }
    }

    /// <summary>
    /// Starts/stops the update timer based on playback status.
    /// Runs when actively playing to continuously update progress bar and synced lyrics.
    /// </summary>
    private void UpdateTimerStates(PlaybackStatus status)
    {
        var shouldRun = status == PlaybackStatus.Playing;

        if (shouldRun && !_lyricsTimer.IsEnabled)
        {
            _lyricsTimer.Start();
            UpdateActiveLyric();
            UpdatePlaybackProgress();
        }
        else if (!shouldRun && _lyricsTimer.IsEnabled)
        {
            _lyricsTimer.Stop();
            UpdateActiveLyric();
            UpdatePlaybackProgress();
        }
    }

    /// <summary>
    /// Updates playback progress bar width and elapsed / total duration labels.
    /// </summary>
    private void UpdatePlaybackProgress()
    {
        if (!_isExpanded) return;

        var pos = _media.CurrentPosition;
        var end = _media.CurrentEndTime;

        if (end <= TimeSpan.Zero || !_media.Current.HasMedia)
        {
            PositionText.Text = "0:00";
            DurationText.Text = "0:00";
            ProgressBarFill.Width = 0;
            return;
        }

        PositionText.Text = FormatTimeSpan(pos);
        DurationText.Text = FormatTimeSpan(end);

        double trackWidth = ProgressBarTrack.ActualWidth;
        if (trackWidth <= 0 && ExpandedContent.ActualWidth > 0)
        {
            // Fallback before initial track layout pass completes
            trackWidth = ExpandedContent.ActualWidth;
        }

        if (trackWidth > 0)
        {
            double fraction = Math.Clamp(pos.TotalSeconds / end.TotalSeconds, 0.0, 1.0);
            ProgressBarFill.Width = fraction * trackWidth;
        }
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private async void OnProgressBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var end = _media.CurrentEndTime;
        if (end <= TimeSpan.Zero || !_media.Current.HasMedia) return;

        double clickX = e.GetPosition(ProgressBarTrack).X;
        double trackWidth = ProgressBarTrack.ActualWidth;
        if (trackWidth <= 0) return;

        double fraction = Math.Clamp(clickX / trackWidth, 0.0, 1.0);
        var targetPosition = TimeSpan.FromSeconds(fraction * end.TotalSeconds);

        // Optimistically update progress fill and label
        ProgressBarFill.Width = fraction * trackWidth;
        PositionText.Text = FormatTimeSpan(targetPosition);

        await _media.TryChangePlaybackPositionAsync(targetPosition);
    }

    /// <summary>
    /// Truncates plain lyrics to the first N non-empty lines for static display.
    /// </summary>
    private static string TruncatePlain(string plain, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "";
        var lines = plain.Split(new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.RemoveEmptyEntries);
        var taken = lines.Length > maxLines
            ? lines[..maxLines]
            : lines;
        return string.Join(" · ", taken).Trim();
    }

    private void ApplyMediaSnapshot(MediaSnapshot snap)
    {
        if (!snap.HasMedia)
        {
            TitleText.Text = "No media playing";
            SubtitleText.Text = "Ready";
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkPlaceholder.Visibility = Visibility.Visible;
            ActiveLyricText.Text = "";
            NextLyricText.Text = "";
            _lastActiveLyricIndex = -2;
            ApplyStatus(snap.Status);
            ApplyControls(snap.Controls);
            return;
        }

        if (TitleText.Text != snap.Title)
        {
            _lastActiveLyricIndex = -2;
            if (_currentLyrics is null)
            {
                ActiveLyricText.Text = "Looking for lyrics...";
                NextLyricText.Text = "";
            }
        }

        TitleText.Text = snap.Title;
        SubtitleText.Text = string.IsNullOrWhiteSpace(snap.Source)
            ? "Unknown source"
            : snap.Source;

        if (snap.Artwork is not null)
        {
            ArtworkImage.Source = snap.Artwork;
            ArtworkImage.Visibility = Visibility.Visible;
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            ArtworkImage.Source = null;
            ArtworkImage.Visibility = Visibility.Collapsed;
            ArtworkPlaceholder.Visibility = Visibility.Visible;
        }

        ApplyStatus(snap.Status);
        ApplyControls(snap.Controls);
    }

    private void ApplyStatus(PlaybackStatus status)
    {
        // Color-code the status dot: green = playing, amber = paused, dim = other.
        StatusDot.Fill = status switch
        {
            PlaybackStatus.Playing => (System.Windows.Media.Brush)FindResource("IslandAccent"),
            PlaybackStatus.Paused  => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE0, 0xA8, 0x00)),
            _                       => (System.Windows.Media.Brush)FindResource("IslandSubtle"),
        };

        // Toggle the play/pause icon: show ⏸ when playing (so clicking pauses),
        // ▶ otherwise (so clicking plays). Keep the button enabled only when
        // the session reports the relevant control as supported — done in
        // ApplyControls — but still reflect the icon here regardless.
        PlayPauseButton.Content = status == PlaybackStatus.Playing ? "⏸" : "▶";
    }

    /// <summary>
    /// Enables/disables the control buttons based on what the current session
    /// reports as supported via GSMTC's Controls flags. When there is no
    /// session, all buttons are disabled.
    /// </summary>
    private void ApplyControls(MediaControlsSupported controls)
    {
        PlayPauseButton.IsEnabled = controls.IsPlayPauseEnabled;
        PrevButton.IsEnabled = controls.IsPreviousEnabled;
        NextButton.IsEnabled = controls.IsNextEnabled;
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        await _media.TryPlayPauseAsync();
        // The status update arrives via the PlaybackInfoChanged event, which
        // will refresh the icon. No need to optimistically flip it here.
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        await _media.TrySkipNextAsync();
    }

    private async void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        await _media.TrySkipPreviousAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNoActivateStyle();
        CenterOnScreen();
    }

    /// <summary>
    /// Sets WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW so the island never steals focus
    /// and doesn't appear in Alt+Tab.
    /// </summary>
    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// Computes the horizontal screen coordinate to keep the island centered at the top.
    /// Accounts for system DPI scaling to avoid positioning jumps or drift.
    /// </summary>
    private double GetScreenCenterLeft(double targetWidth)
    {
        double dpiScale = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiScale = source.CompositionTarget.TransformToDevice.M11;
        }
        else
        {
            try
            {
                dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
            catch
            {
                dpiScale = 1.0;
            }
        }

        var screen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        double screenWidth = screen.WorkingArea.Width / dpiScale;
        return (screenWidth - targetWidth) / 2.0;
    }

    /// <summary>
    /// Centers the pill horizontally near the top of the primary screen.
    /// </summary>
    private void CenterOnScreen()
    {
        double targetWidth = _isExpanded ? ExpandedWidth : CompactWidth;
        Width = targetWidth;
        Height = _isExpanded ? ExpandedHeight : CompactHeight;
        Left = GetScreenCenterLeft(targetWidth);
        Top = 8; // slight offset from top edge
    }

    private void OnIslandClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                // If user clicked inside an interactive button (⏮, ▶/⏸, ⏭) or progress bar, do not toggle mode
                if (FindVisualParent<System.Windows.Controls.Button>(dep) != null ||
                    FindVisualParent<System.Windows.Controls.Grid>(dep) == ProgressBarTrack)
                    return;
            }

            if (_isExpanded)
                Collapse();
            else
                Expand();
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current != null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void Expand()
    {
        if (_isExpanded) return;
        _isExpanded = true;
        _lastActiveLyricIndex = -2;
        UpdateActiveLyric();
        UpdatePlaybackProgress();
        TransitionToMode(true);
    }

    private void Collapse()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        TransitionToMode(false);
    }

    /// <summary>
    /// Fluidly animates the Dynamic Island pill between Compact (Clock) and Expanded (Music/Lyrics) modes.
    /// Coordinates simultaneous width, height, centering, crossfade, and 10px vertical parallax motion.
    /// Monotonically tracks transition tokens so rapid clicks cancel and reverse cleanly without flicker.
    /// </summary>
    private void TransitionToMode(bool toExpanded)
    {
        int token = ++_transitionCounter;

        double targetWidth = toExpanded ? ExpandedWidth : CompactWidth;
        double targetHeight = toExpanded ? ExpandedHeight : CompactHeight;
        double targetLeft = GetScreenCenterLeft(targetWidth);

        // 1. Dimensions and Centering Animations (synchronized easing ensures exact center alignment at every frame)
        var widthAnim = new DoubleAnimation(targetWidth, TransitionDuration) { EasingFunction = GeometryEase };
        var heightAnim = new DoubleAnimation(targetHeight, TransitionDuration) { EasingFunction = GeometryEase };
        var leftAnim = new DoubleAnimation(targetLeft, TransitionDuration) { EasingFunction = GeometryEase };

        // 2. Prepare visual elements for crossfade and slide
        if (toExpanded)
        {
            // Transitioning from Compact (Clock) to Expanded (Music/Lyrics)
            CompactContent.IsHitTestVisible = false;
            ExpandedContent.IsHitTestVisible = true;

            // Ensure ExpandedContent is in the visual tree and ready
            if (ExpandedContent.Visibility != Visibility.Visible)
            {
                ExpandedContent.Visibility = Visibility.Visible;
                ExpandedContent.Opacity = 0;
                ExpandedTransform.Y = VerticalSlideDistance; // start +10px below
            }

            // CompactContent: fade out (1 -> 0) and slide up (0 -> -10px)
            var compactFade = new DoubleAnimation(0.0, TransitionDuration) { EasingFunction = ContentEase };
            var compactSlide = new DoubleAnimation(-VerticalSlideDistance, TransitionDuration) { EasingFunction = ContentEase };

            // ExpandedContent: fade in (0 -> 1) and slide up into place (+10px -> 0)
            var expandedFade = new DoubleAnimation(1.0, TransitionDuration) { EasingFunction = ContentEase };
            var expandedSlide = new DoubleAnimation(0.0, TransitionDuration) { EasingFunction = ContentEase };

            CompactContent.BeginAnimation(UIElement.OpacityProperty, compactFade, HandoffBehavior.SnapshotAndReplace);
            CompactTransform.BeginAnimation(TranslateTransform.YProperty, compactSlide, HandoffBehavior.SnapshotAndReplace);

            ExpandedContent.BeginAnimation(UIElement.OpacityProperty, expandedFade, HandoffBehavior.SnapshotAndReplace);
            ExpandedTransform.BeginAnimation(TranslateTransform.YProperty, expandedSlide, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            // Transitioning from Expanded (Music/Lyrics) to Compact (Clock)
            ExpandedContent.IsHitTestVisible = false;
            CompactContent.IsHitTestVisible = true;

            // Ensure CompactContent is in the visual tree and ready
            if (CompactContent.Visibility != Visibility.Visible)
            {
                CompactContent.Visibility = Visibility.Visible;
                CompactContent.Opacity = 0;
                CompactTransform.Y = -VerticalSlideDistance; // start -10px above
            }

            // ExpandedContent: fade out (1 -> 0) and slide down (0 -> +10px)
            var expandedFade = new DoubleAnimation(0.0, TransitionDuration) { EasingFunction = ContentEase };
            var expandedSlide = new DoubleAnimation(VerticalSlideDistance, TransitionDuration) { EasingFunction = ContentEase };

            // CompactContent: fade in (0 -> 1) and slide down into place (-10px -> 0)
            var compactFade = new DoubleAnimation(1.0, TransitionDuration) { EasingFunction = ContentEase };
            var compactSlide = new DoubleAnimation(0.0, TransitionDuration) { EasingFunction = ContentEase };

            ExpandedContent.BeginAnimation(UIElement.OpacityProperty, expandedFade, HandoffBehavior.SnapshotAndReplace);
            ExpandedTransform.BeginAnimation(TranslateTransform.YProperty, expandedSlide, HandoffBehavior.SnapshotAndReplace);

            CompactContent.BeginAnimation(UIElement.OpacityProperty, compactFade, HandoffBehavior.SnapshotAndReplace);
            CompactTransform.BeginAnimation(TranslateTransform.YProperty, compactSlide, HandoffBehavior.SnapshotAndReplace);
        }

        // 3. Completion Callback guarded by monotonic token
        widthAnim.Completed += (_, _) =>
        {
            // If superseded by a rapid subsequent click, ignore completion of this superseded animation
            if (token != _transitionCounter) return;

            // Finalize visual elements
            if (toExpanded)
            {
                CompactContent.Visibility = Visibility.Collapsed;
                CompactContent.Opacity = 0;
                CompactTransform.Y = 0;

                ExpandedContent.Visibility = Visibility.Visible;
                ExpandedContent.Opacity = 1;
                ExpandedTransform.Y = 0;
            }
            else
            {
                ExpandedContent.Visibility = Visibility.Collapsed;
                ExpandedContent.Opacity = 0;
                ExpandedTransform.Y = 0;

                CompactContent.Visibility = Visibility.Visible;
                CompactContent.Opacity = 1;
                CompactTransform.Y = 0;
            }

            // Remove animation clocks and enforce exact dimensions
            Width = targetWidth;
            Height = targetHeight;
            Left = targetLeft;

            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            BeginAnimation(LeftProperty, null);

            CompactContent.BeginAnimation(UIElement.OpacityProperty, null);
            CompactTransform.BeginAnimation(TranslateTransform.YProperty, null);
            ExpandedContent.BeginAnimation(UIElement.OpacityProperty, null);
            ExpandedTransform.BeginAnimation(TranslateTransform.YProperty, null);

            Width = targetWidth;
            Height = targetHeight;
            Left = targetLeft;

            if (toExpanded)
            {
                CompactContent.Opacity = 0;
                CompactTransform.Y = 0;
                ExpandedContent.Opacity = 1;
                ExpandedTransform.Y = 0;
            }
            else
            {
                ExpandedContent.Opacity = 0;
                ExpandedTransform.Y = 0;
                CompactContent.Opacity = 1;
                CompactTransform.Y = 0;
            }
        };

        // Start window geometry animations simultaneously
        BeginAnimation(WidthProperty, widthAnim, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(HeightProperty, heightAnim, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(LeftProperty, leftAnim, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _lyricsTimer.Stop();
        _lyrics.LyricsChanged -= OnLyricsChanged;
        _lyrics.Dispose();
        _lyricsProvider.Dispose();
        _media.MediaChanged -= OnMediaChanged;
        _media.PlaybackStatusChanged -= OnPlaybackStatusChanged;
        _media.TimelineChanged -= OnTimelineChanged;
        _media.Dispose();
        base.OnClosed(e);
    }
}
