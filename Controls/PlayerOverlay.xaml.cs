using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Mio.Player;

namespace Mio.Controls;

public sealed partial class PlayerOverlay : UserControl
{
    private static readonly Thickness DefaultBottomContentMargin = new(20, 8, 20, 16);

    private readonly DispatcherQueueTimer _progressTimer;
    private bool _suppressUpdates;
    private bool _isSeeking;
    private Thickness _lastBottomContentMargin = DefaultBottomContentMargin;
    private DateTimeOffset _lastProgressUpdateTime;
    private double _lastProgressPosition;
    private double _lastProgressDuration;
    private bool _isProgressInterpolating;

    public PlayerOverlay()
    {
        InitializeComponent();

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(33);
        _progressTimer.Tick += (_, _) => UpdateInterpolatedProgress();
        Unloaded += (_, _) => _progressTimer.Stop();
    }

    public event EventHandler? PlayPauseRequested;
    public event EventHandler<SeekRequestedEventArgs>? SeekRequested;
    public event EventHandler<VolumeRequestedEventArgs>? VolumeRequested;
    public event EventHandler? FullscreenRequested;

    public UIElement TitleDragArea => TitleDragSurface;

    public bool IsPointerWithin { get; private set; }

    public bool IsDragging => _isSeeking;

    public void SetVideoContentInset(double horizontalInset)
    {
        SetVideoContentInsets(horizontalInset, horizontalInset);
    }

    public void SetVideoContentInsets(double leftInset, double rightInset)
    {
        var margin = new Thickness(
            DefaultBottomContentMargin.Left + Math.Max(0, leftInset),
            DefaultBottomContentMargin.Top,
            DefaultBottomContentMargin.Right + Math.Max(0, rightInset),
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
        _suppressUpdates = true;
        try
        {
            TitleText.Text = string.IsNullOrWhiteSpace(state.MediaTitle) ? "Mio" : state.MediaTitle;
            PlayPauseIcon.Glyph = state.IsPaused ? "\uE768" : "\uE769";
            PlayPauseButton.IsEnabled = state.HasMedia;

            var canSeek = state.HasMedia && !state.IsIdleActive && state.Duration > 0;
            SeekSlider.IsEnabled = canSeek;
            SeekSlider.Maximum = canSeek ? Math.Max(1, state.Duration) : 1;
            DurationText.Text = FormatTime(state.Duration);

            _lastProgressPosition = canSeek ? Clamp(state.Position, 0, SeekSlider.Maximum) : 0;
            _lastProgressDuration = canSeek ? state.Duration : 0;
            _lastProgressUpdateTime = DateTimeOffset.UtcNow;
            _isProgressInterpolating = canSeek && !state.IsPaused;
            CurrentTimeText.Text = FormatTime(_lastProgressPosition);

            if (!_isSeeking)
            {
                SeekSlider.Value = _lastProgressPosition;
            }

            VolumeSlider.IsEnabled = state.HasMedia;
            VolumeSlider.Value = Clamp(state.Volume, 0, 100);

            FullscreenIcon.Glyph = state.IsFullscreen ? "\uE73F" : "\uE740";
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

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!SeekSlider.IsEnabled)
        {
            return;
        }

        _isSeeking = true;
        UpdateProgressTimer();
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CommitSeek();
    }

    private void SeekSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = false;
        UpdateProgressTimer();
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
        if (_isProgressInterpolating && !_isSeeking)
        {
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }
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
