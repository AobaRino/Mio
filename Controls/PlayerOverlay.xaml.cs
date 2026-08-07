using System;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Mio.Player;

namespace Mio.Controls;

public sealed partial class PlayerOverlay : UserControl
{
    private const double SeekTrackHorizontalInset = 10;
    private const double ChromeFadeInMilliseconds = 120;
    private const double ChromeFadeOutMilliseconds = 220;
    private static readonly Thickness DefaultBottomContentMargin = new(20, 8, 20, 16);
    private static readonly GridLength CaptionButtonColumnWidth = new(150);

    private readonly DispatcherQueueTimer _progressTimer;
    private readonly Visual _chromeVisual;
    private readonly Compositor _compositor;
    private uint? _activeSeekPointerId;
    private bool _suppressUpdates;
    private bool _isSeeking;
    private bool _isChromeVisible = true;
    private bool _collapseChromeWhenHidden;
    private long _chromeFadeGeneration;
    private Thickness _lastBottomContentMargin = DefaultBottomContentMargin;
    private DateTimeOffset _lastProgressUpdateTime;
    private double _lastProgressPosition;
    private double _lastProgressDuration;
    private bool _isProgressInterpolating;
    private PlayerState _currentState = PlayerState.CreateIdle();

    public PlayerOverlay()
    {
        InitializeComponent();

        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SeekSlider_PointerMoved), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(SeekSlider_PointerCanceled), true);

        _chromeVisual = ElementCompositionPreview.GetElementVisual(OverlayRoot);
        _compositor = _chromeVisual.Compositor;

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(33);
        _progressTimer.Tick += (_, _) => UpdateInterpolatedProgress();
        Unloaded += (_, _) => _progressTimer.Stop();
    }

    public event EventHandler? PlayPauseRequested;
    public event EventHandler<SeekRequestedEventArgs>? SeekRequested;
    public event EventHandler<VolumeRequestedEventArgs>? VolumeRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler<TrackRequestedEventArgs>? SubtitleTrackRequested;
    public event EventHandler? SubtitleOffRequested;
    public event EventHandler? SubtitleAutoRequested;
    public event EventHandler? ExternalSubtitleRequested;
    public event EventHandler<TrackRequestedEventArgs>? AudioTrackRequested;

    public UIElement TitleDragArea => TitleDragSurface;

    public bool IsPointerWithin { get; private set; }

    public bool IsDragging => _isSeeking;

    public bool IsChromeVisible => _isChromeVisible;

    public void ShowChrome()
    {
        SetChromeVisible(true);
    }

    public void HideChrome()
    {
        SetChromeVisible(false);
    }

    private void SetChromeVisible(bool visible)
    {
        if (_isChromeVisible == visible)
        {
            return;
        }

        _isChromeVisible = visible;
        var generation = ++_chromeFadeGeneration;

        if (visible)
        {
            OverlayRoot.Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            StartChromeFade(1, ChromeFadeInMilliseconds, generation);
        }
        else
        {
            IsHitTestVisible = false;
            IsPointerWithin = false;
            StartChromeFade(0, ChromeFadeOutMilliseconds, generation);
        }

        UpdateProgressTimer();
    }

    private void StartChromeFade(float targetOpacity, double milliseconds, long generation)
    {
        // 只插入终点关键帧，起点隐式取当前视觉值，淡出中途被唤醒时能平滑接续。
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(
            1,
            targetOpacity,
            _compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(milliseconds);

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _chromeVisual.StartAnimation(nameof(Visual.Opacity), animation);
        batch.End();
        batch.Completed += (_, _) => OnChromeFadeCompleted(generation);
    }

    private void OnChromeFadeCompleted(long generation)
    {
        // 淡出途中可能又被唤醒，此时不能再退出合成树。
        if (generation != _chromeFadeGeneration || _isChromeVisible || !_collapseChromeWhenHidden)
        {
            return;
        }

        OverlayRoot.Visibility = Visibility.Collapsed;
    }

    // 只在全屏时把 overlay 折叠出合成树：这是最需要让 DWM 把视频层提升到
    // MPO / independent flip 的场景，而窗口模式下 TitleDragArea 折叠后
    // SetTitleBar 的拖动区会一并失效，所以窗口模式保持 Opacity=0。
    private void UpdateChromeCollapsePolicy(bool isFullscreen)
    {
        if (_collapseChromeWhenHidden == isFullscreen)
        {
            return;
        }

        _collapseChromeWhenHidden = isFullscreen;
        if (_isChromeVisible)
        {
            return;
        }

        OverlayRoot.Visibility = isFullscreen ? Visibility.Collapsed : Visibility.Visible;
    }

    // 全屏时系统 caption buttons 不显示，预留列会白白挤窄标题。
    private void UpdateCaptionButtonColumn(bool isFullscreen)
    {
        var width = isFullscreen ? new GridLength(0) : CaptionButtonColumnWidth;
        if (CaptionButtonColumn.Width.GridUnitType == width.GridUnitType &&
            Math.Abs(CaptionButtonColumn.Width.Value - width.Value) < 0.5)
        {
            return;
        }

        CaptionButtonColumn.Width = width;
    }

    public void SetVideoContentInset(double horizontalInset)
    {
        var inset = Math.Max(0, horizontalInset);
        var margin = new Thickness(
            DefaultBottomContentMargin.Left + inset,
            DefaultBottomContentMargin.Top,
            DefaultBottomContentMargin.Right + inset,
            DefaultBottomContentMargin.Bottom);

        if (Math.Abs(margin.Left - _lastBottomContentMargin.Left) < 0.5 &&
            Math.Abs(margin.Right - _lastBottomContentMargin.Right) < 0.5)
        {
            return;
        }

        BottomContent.Margin = margin;
        _lastBottomContentMargin = margin;
    }

    public void ApplyState(PlayerState state)
    {
        _currentState = state;
        _suppressUpdates = true;
        try
        {
            TitleText.Text = string.IsNullOrWhiteSpace(state.MediaTitle) ? "Mio" : state.MediaTitle;
            PlayPauseIcon.Glyph = state.IsPaused || state.IsEndOfFile ? "\uE768" : "\uE769";
            PlayPauseButton.IsEnabled = state.HasMedia;

            var canSeek = state.HasMedia && !state.IsIdleActive && state.Duration > 0;
            SeekSlider.IsEnabled = canSeek;
            SeekSlider.Maximum = canSeek ? Math.Max(1, state.Duration) : 1;
            DurationText.Text = FormatTime(state.Duration);

            _lastProgressPosition = canSeek ? Clamp(state.Position, 0, SeekSlider.Maximum) : 0;
            _lastProgressDuration = canSeek ? state.Duration : 0;
            _lastProgressUpdateTime = DateTimeOffset.UtcNow;
            _isProgressInterpolating = canSeek && !state.IsPaused && !state.IsEndOfFile;
            CurrentTimeText.Text = FormatTime(_lastProgressPosition);

            if (!_isSeeking)
            {
                SeekSlider.Value = _lastProgressPosition;
            }

            VolumeSlider.IsEnabled = state.HasMedia;
            VolumeSlider.Value = Clamp(state.Volume, 0, 100);
            SubtitleButton.IsEnabled = state.HasMedia;
            AudioTrackButton.IsEnabled = state.HasMedia;

            FullscreenIcon.Glyph = state.IsFullscreen ? "\uE73F" : "\uE740";
            UpdateCaptionButtonColumn(state.IsFullscreen);
            UpdateChromeCollapsePolicy(state.IsFullscreen);
            UpdateProgressTimer();
        }
        finally
        {
            _suppressUpdates = false;
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        PlayPauseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SubtitleButton_Click(object sender, RoutedEventArgs e)
    {
        var hasSubtitleTracks = _currentState.SubtitleTracks.Count > 0;

        var flyout = new MenuFlyout();
        flyout.Items.Add(CreateMenuItem(
            _currentState.SelectedSubtitleTrackId is null ? "✓ Off" : "Off",
            (_, _) => SubtitleOffRequested?.Invoke(this, EventArgs.Empty)));

        // 没有字幕轨时 Auto 选不出任何东西，置灰以免看起来点了没反应。
        var autoItem = CreateMenuItem("Auto", (_, _) => SubtitleAutoRequested?.Invoke(this, EventArgs.Empty));
        autoItem.IsEnabled = hasSubtitleTracks;
        flyout.Items.Add(autoItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        if (!hasSubtitleTracks)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "No subtitle tracks",
                IsEnabled = false
            });
        }

        foreach (var track in _currentState.SubtitleTracks)
        {
            var trackId = track.Id;
            var text = trackId == _currentState.SelectedSubtitleTrackId ? $"✓ {track.DisplayName}" : track.DisplayName;
            flyout.Items.Add(CreateMenuItem(text, (_, _) => SubtitleTrackRequested?.Invoke(this, new TrackRequestedEventArgs(trackId))));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateMenuItem(
            "Load External Subtitle...",
            (_, _) => ExternalSubtitleRequested?.Invoke(this, EventArgs.Empty)));
        flyout.ShowAt(SubtitleButton);
    }

    private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        if (_currentState.AudioTracks.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "No audio tracks",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var track in _currentState.AudioTracks)
            {
                var trackId = track.Id;
                var text = trackId == _currentState.SelectedAudioTrackId ? $"✓ {track.DisplayName}" : track.DisplayName;
                flyout.Items.Add(CreateMenuItem(text, (_, _) => AudioTrackRequested?.Invoke(this, new TrackRequestedEventArgs(trackId))));
            }
        }

        flyout.ShowAt(AudioTrackButton);
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!SeekSlider.IsEnabled)
        {
            return;
        }

        _activeSeekPointerId = e.Pointer.PointerId;
        _isSeeking = true;
        UpdateSeekValueFromPointer(e);
        UpdateProgressTimer();
        e.Handled = true;
    }

    private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking || _activeSeekPointerId != e.Pointer.PointerId)
        {
            return;
        }

        UpdateSeekValueFromPointer(e);
        e.Handled = true;
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking || _activeSeekPointerId != e.Pointer.PointerId)
        {
            return;
        }

        UpdateSeekValueFromPointer(e);
        _activeSeekPointerId = null;
        CommitSeek();
        e.Handled = true;
    }

    private void SeekSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_activeSeekPointerId != e.Pointer.PointerId)
        {
            return;
        }

        _activeSeekPointerId = null;
        _isSeeking = false;
        UpdateProgressTimer();
        e.Handled = true;
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUpdates)
        {
            return;
        }

        if (_isSeeking)
        {
            CurrentTimeText.Text = FormatTime(e.NewValue);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUpdates)
        {
            return;
        }

        VolumeRequested?.Invoke(this, new VolumeRequestedEventArgs(e.NewValue));
    }

    private void UpdateSeekValueFromPointer(PointerRoutedEventArgs e)
    {
        var trackWidth = Math.Max(1, SeekSlider.ActualWidth - (SeekTrackHorizontalInset * 2));
        var pointerX = e.GetCurrentPoint(SeekSlider).Position.X - SeekTrackHorizontalInset;
        var ratio = Clamp(pointerX / trackWidth, 0, 1);
        SeekSlider.Value = SeekSlider.Minimum + ((SeekSlider.Maximum - SeekSlider.Minimum) * ratio);
    }

    private void CommitSeek()
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        _lastProgressPosition = SeekSlider.Value;
        _lastProgressUpdateTime = DateTimeOffset.UtcNow;
        UpdateProgressTimer();
        SeekRequested?.Invoke(this, new SeekRequestedEventArgs(SeekSlider.Value));
    }

    private void UpdateInterpolatedProgress()
    {
        if (_suppressUpdates || _isSeeking || !_isProgressInterpolating || _lastProgressDuration <= 0)
        {
            return;
        }

        var elapsed = (DateTimeOffset.UtcNow - _lastProgressUpdateTime).TotalSeconds;
        var position = Clamp(_lastProgressPosition + elapsed, 0, _lastProgressDuration);

        _suppressUpdates = true;
        try
        {
            SeekSlider.Value = position;
            CurrentTimeText.Text = FormatTime(position);
        }
        finally
        {
            _suppressUpdates = false;
        }
    }

    private void UpdateProgressTimer()
    {
        // overlay 隐藏时进度条不可见，30Hz 插值只是白白占用 UI 线程。
        if (_isProgressInterpolating && !_isSeeking && _isChromeVisible)
        {
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }
    }

    private static MenuFlyoutItem CreateMenuItem(string text, RoutedEventHandler clickHandler)
    {
        var item = new MenuFlyoutItem
        {
            Text = string.IsNullOrWhiteSpace(text) ? "Untitled Track" : text
        };
        item.Click += clickHandler;
        return item;
    }

    private void OverlayRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        IsPointerWithin = true;
    }

    private void OverlayRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        IsPointerWithin = false;
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            return "0:00";
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)time.TotalMinutes, time.Seconds);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Min(max, Math.Max(min, value));
    }
}

public sealed class SeekRequestedEventArgs : EventArgs
{
    public SeekRequestedEventArgs(double position)
    {
        Position = position;
    }

    public double Position { get; }
}

public sealed class VolumeRequestedEventArgs : EventArgs
{
    public VolumeRequestedEventArgs(double volume)
    {
        Volume = volume;
    }

    public double Volume { get; }
}

public sealed class TrackRequestedEventArgs : EventArgs
{
    public TrackRequestedEventArgs(int trackId)
    {
        TrackId = trackId;
    }

    public int TrackId { get; }
}
