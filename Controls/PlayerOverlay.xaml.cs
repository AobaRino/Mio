using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Mio.Player;

namespace Mio.Controls;

public sealed partial class PlayerOverlay : UserControl
{
    private bool _suppressUpdates;
    private bool _isSeeking;

    public PlayerOverlay()
    {
        InitializeComponent();
    }

    public event EventHandler? PlayPauseRequested;
    public event EventHandler<SeekRequestedEventArgs>? SeekRequested;
    public event EventHandler<VolumeRequestedEventArgs>? VolumeRequested;
    public event EventHandler? FullscreenRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? MaximizeRestoreRequested;
    public event EventHandler? CloseRequested;

    public UIElement TitleDragArea => TitleDragSurface;

    public bool IsPointerWithin { get; private set; }

    public bool IsDragging => _isSeeking;

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
            CurrentTimeText.Text = FormatTime(state.Position);

            if (!_isSeeking)
            {
                SeekSlider.Value = canSeek ? Clamp(state.Position, 0, SeekSlider.Maximum) : 0;
            }

            VolumeSlider.IsEnabled = state.HasMedia;
            VolumeSlider.Value = Clamp(state.Volume, 0, 100);

            FullscreenIcon.Glyph = state.IsFullscreen ? "\uE73F" : "\uE740";
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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        MaximizeRestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!SeekSlider.IsEnabled)
        {
            return;
        }

        _isSeeking = true;
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CommitSeek();
    }

    private void SeekSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = false;
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
        SeekRequested?.Invoke(this, new SeekRequestedEventArgs(SeekSlider.Value));
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
