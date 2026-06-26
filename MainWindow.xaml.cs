using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Mio.Interop;
using Mio.Player;
using Mio.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT.Interop;

namespace Mio;

public sealed partial class MainWindow : Window
{
    private readonly MpvPlayer _player = new();
    private readonly FullscreenService _fullscreenService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _overlayHideTimer;
    private readonly IntPtr _hwnd;

    private IntPtr _boundSwapChain;
    private PlayerState _lastState = PlayerState.CreateIdle();
    private bool _hasVisibleError;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _fullscreenService = new FullscreenService(this);
        _fullscreenService.FullscreenChanged += OnFullscreenChanged;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Overlay.TitleDragArea);
        ConfigureSystemCaptionButtons();

        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
        Closed += MainWindow_Closed;

        Overlay.PlayPauseRequested += (_, _) => _player.TogglePause();
        Overlay.SeekRequested += (_, e) => _player.SeekAbsolute(e.Position);
        Overlay.VolumeRequested += (_, e) => _player.SetVolume(e.Volume);
        Overlay.FullscreenRequested += (_, _) => ToggleFullscreen();

        _player.StateChanged += OnPlayerStateChanged;
        _player.SwapChainChanged += OnSwapChainChanged;
        _player.ErrorOccurred += ShowError;

        _overlayHideTimer = DispatcherQueue.CreateTimer();
        _overlayHideTimer.Interval = TimeSpan.FromSeconds(3);
        _overlayHideTimer.Tick += (_, _) => HideOverlayWhenIdle();

        Overlay.ApplyState(_lastState);
        ShowOverlay();

        try
        {
            _player.Initialize();
        }
        catch (Exception ex) when (ex is MpvException or DllNotFoundException or BadImageFormatException)
        {
            ShowError(ex.Message);
        }
    }

    private void ConfigureSystemCaptionButtons()
    {
        try
        {
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            var titleBar = AppWindow.GetFromWindowId(windowId).TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xEA, 0xF5, 0xF5, 0xF5);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x99, 0xF5, 0xF5, 0xF5);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mio.WinUI] configure caption buttons failed: {ex.Message}");
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var path = await FileOpenService.TryGetFirstFilePathAsync(e.DataView);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await LoadFileAsync(path);
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Space:
                _player.TogglePause();
                ShowOverlay();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                if (_fullscreenService.IsFullscreen)
                {
                    _fullscreenService.ExitFullscreen();
                    e.Handled = true;
                }
                break;
            case VirtualKey.Left:
                _player.SeekRelative(-InputService.SeekStepSeconds);
                ShowOverlay();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                _player.SeekRelative(InputService.SeekStepSeconds);
                ShowOverlay();
                e.Handled = true;
                break;
            case VirtualKey.Up:
                _player.SetVolume(_lastState.Volume + InputService.VolumeStep);
                ShowOverlay();
                e.Handled = true;
                break;
            case VirtualKey.Down:
                _player.SetVolume(_lastState.Volume - InputService.VolumeStep);
                ShowOverlay();
                e.Handled = true;
                break;
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowOverlay();
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        _player.SetVolume(_lastState.Volume + Math.Sign(delta) * InputService.VolumeStep);
        ShowOverlay();
        e.Handled = true;
    }

    private void VideoPanel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleFullscreen();
        e.Handled = true;
    }

    private void VideoPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[Mio.WinUI] VideoPanel loaded");
        UpdateCompositionSizeFromPanel();
        UpdateOverlayViewportInsets();
    }

    private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Debug.WriteLine("[Mio.WinUI] panel size changed");
        UpdateCompositionSizeFromPanel();
        UpdateOverlayViewportInsets();
    }

    private async Task LoadFileAsync(string path)
    {
        ClearError();
        ShowOverlay();

        try
        {
            UpdateCompositionSizeFromPanel();
            UpdateOverlayViewportInsets();
            await _player.LoadAsync(path);
            UpdateCompositionSizeFromPanel();
            UpdateOverlayViewportInsets();
        }
        catch (Exception ex) when (ex is MpvException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }

    private void OnPlayerStateChanged(PlayerState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lastState = state;
            _lastState.IsFullscreen = _fullscreenService.IsFullscreen;
            Overlay.ApplyState(_lastState);
            UpdateOverlayViewportInsets();
            UpdateIdleLayer();

            if (!_lastState.HasMedia || _lastState.IsPaused)
            {
                ShowOverlay();
            }
        });
    }

    private void OnSwapChainChanged(IntPtr swapChain)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (swapChain == IntPtr.Zero || swapChain == _boundSwapChain)
            {
                return;
            }

            var hr = SwapChainPanelInterop.SetSwapChain(VideoPanel, swapChain);
            Debug.WriteLine($"[Mio.WinUI] SetSwapChain result={ComHelpers.FormatHResult(hr)}");
            if (ComHelpers.Failed(hr))
            {
                if (hr == unchecked((int)0x80004002))
                {
                    ShowError("SwapChainPanel native interop failed: ISwapChainPanelNative not available. Check WinUI 3 dxinterop GUID/interface.");
                    return;
                }

                ShowError($"SetSwapChain failed: HRESULT {ComHelpers.FormatHResult(hr)}");
                return;
            }

            Debug.WriteLine($"[Mio.WinUI] SetSwapChain success ptr=0x{swapChain.ToInt64():X} HRESULT {ComHelpers.FormatHResult(hr)}");
            _boundSwapChain = swapChain;
            UpdateCompositionSizeFromPanel();
            UpdateOverlayViewportInsets();
        });
    }

    private void OnFullscreenChanged(bool isFullscreen)
    {
        _lastState.IsFullscreen = isFullscreen;
        Overlay.ApplyState(_lastState);
        ShowOverlay();
        UpdateCompositionSizeFromPanel();
        UpdateOverlayViewportInsets();
    }

    private void ToggleFullscreen()
    {
        _fullscreenService.ToggleFullscreen();
    }

    private void UpdateCompositionSizeFromPanel()
    {
        if (!VideoPanel.IsLoaded)
        {
            Debug.WriteLine("[Mio.WinUI] d3d11-composition-size skipped: VideoPanel not loaded");
            return;
        }

        var scale = VideoPanel.XamlRoot?.RasterizationScale;
        if (scale is null or <= 0)
        {
            scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        }

        var width = (int)Math.Round(VideoPanel.ActualWidth * scale.Value);
        var height = (int)Math.Round(VideoPanel.ActualHeight * scale.Value);
        if (width <= 0 || height <= 0)
        {
            Debug.WriteLine($"[Mio.WinUI] d3d11-composition-size skipped: invalid panel size dip={VideoPanel.ActualWidth:0.###}x{VideoPanel.ActualHeight:0.###} scale={scale.Value:0.###} pixels={width}x{height}");
            return;
        }

        Debug.WriteLine($"[Mio.WinUI] d3d11-composition-size panel dip={VideoPanel.ActualWidth:0.###}x{VideoPanel.ActualHeight:0.###} scale={scale.Value:0.###} pixels={width}x{height}");
        _player.SetCompositionSize(width, height);
    }

    private void UpdateOverlayViewportInsets()
    {
        var panelWidth = VideoPanel.ActualWidth;
        var panelHeight = VideoPanel.ActualHeight;
        var videoAspectRatio = _lastState.VideoAspectRatio;

        if (panelWidth <= 0 || panelHeight <= 0 || videoAspectRatio <= 0)
        {
            Overlay.SetVideoContentInset(0);
            return;
        }

        var panelAspectRatio = panelWidth / panelHeight;
        var horizontalInset = 0.0;

        if (panelAspectRatio > videoAspectRatio)
        {
            var visibleVideoWidth = panelHeight * videoAspectRatio;
            horizontalInset = (panelWidth - visibleVideoWidth) / 2;
        }

        Overlay.SetVideoContentInset(horizontalInset);
    }

    private void UpdateIdleLayer()
    {
        IdleLayer.Visibility = (!_lastState.HasMedia || _hasVisibleError) ? Visibility.Visible : Visibility.Collapsed;
        IdleTitle.Text = _lastState.HasMedia ? "Playback issue" : "Drop video here";
        StatusText.Text = _lastState.HasMedia
            ? (_lastState.IsSwapChainReady ? "Video surface ready" : "Waiting for D3D11 swapchain")
            : "No media";
    }

    private void ShowError(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _hasVisibleError = true;
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
            UpdateIdleLayer();
            ShowOverlay();
        });
    }

    private void ClearError()
    {
        _hasVisibleError = false;
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateIdleLayer();
    }

    private void ShowOverlay()
    {
        Overlay.Opacity = 1;
        Overlay.IsHitTestVisible = true;
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    private void HideOverlayWhenIdle()
    {
        if (!_lastState.HasMedia || _lastState.IsPaused || Overlay.IsPointerWithin || Overlay.IsDragging)
        {
            return;
        }

        Overlay.Opacity = 0;
        Overlay.IsHitTestVisible = false;
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Debug.WriteLine("[Mio.WinUI] dispose start");
        try
        {
            if (_boundSwapChain != IntPtr.Zero)
            {
                _ = SwapChainPanelInterop.SetSwapChain(VideoPanel, IntPtr.Zero);
                _boundSwapChain = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mio.WinUI] SetSwapChain clear failed: {ex.Message}");
        }

        _player.Dispose();
        Debug.WriteLine("[Mio.WinUI] dispose complete");
    }
}
