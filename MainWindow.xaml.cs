using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
using Windows.Storage.Pickers;
using WinRT.Interop;
using static Mio.Diagnostics.MioLog;

namespace Mio;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan OverlayHideDelay = TimeSpan.FromSeconds(3);

    private readonly MpvPlayer _player = new();
    private readonly FullscreenService _fullscreenService;
    private readonly SwapChainBinder _swapChainBinder;
    // 必须全限定：Windows.System 下也有同名的 DispatcherQueueTimer，两个 using 都在。
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _overlayHideTimer;
    private readonly IntPtr _hwnd;

    private CancellationTokenSource? _loadCancellation;
    private PlayerState _lastState = PlayerState.CreateIdle();
    private long _loadGeneration;
    private int _swapChainRecoveryAttempts;
    private bool _hasVisibleError;
    private bool _isRecoveringSwapChain;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _fullscreenService = new FullscreenService(this);
        _fullscreenService.FullscreenChanged += OnFullscreenChanged;

        _swapChainBinder = new SwapChainBinder(VideoPanel, UpdateCompositionSizeFromPanel);
        _swapChainBinder.Completed += OnSwapChainBindCompleted;

        // 与 RootGrid 的 #050505 一致，让 XAML 布局跟上尺寸变化前露出的那一帧不刺眼。
        NativeMethods.SetWindowBackgroundColor(_hwnd, 0x00050505);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Overlay.TitleDragArea);
        ConfigureSystemCaptionButtons();

        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
        Closed += MainWindow_Closed;

        Overlay.PlayPauseRequested += (_, _) => _player.TogglePause();
        Overlay.SeekRequested += (_, e) => _player.SeekAbsolute(e.Position);
        Overlay.VolumeRequested += (_, e) => _player.SetVolume(e.Volume);
        Overlay.MuteRequested += (_, _) => _player.ToggleMute();
        Overlay.FullscreenRequested += (_, _) => ToggleFullscreen();
        Overlay.SubtitleTrackRequested += (_, e) => _player.SelectSubtitleTrack(e.TrackId);
        Overlay.SubtitleOffRequested += (_, _) => _player.DisableSubtitles();
        Overlay.SubtitleAutoRequested += (_, _) => _player.AutoSelectSubtitles();
        Overlay.ExternalSubtitleRequested += async (_, _) => await LoadExternalSubtitleAsync();
        Overlay.AudioTrackRequested += (_, e) => _player.SelectAudioTrack(e.TrackId);

        _player.StateChanged += OnPlayerStateChanged;
        _player.SwapChainChanged += OnSwapChainChanged;
        _player.ErrorOccurred += ShowError;

        _overlayHideTimer = DispatcherQueue.CreateTimer();
        _overlayHideTimer.Interval = OverlayHideDelay;
        _overlayHideTimer.Tick += (_, _) => HideOverlayWhenIdle();

        Overlay.ApplyState(_lastState, isFullscreen: false);
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
            Log($"configure caption buttons failed: {ex.Message}");
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        try
        {
            var source = await FileOpenService.TryGetFirstMediaSourceAsync(e.DataView);
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            await LoadFileAsync(source);
        }
        catch (Exception ex)
        {
            // async void：这里逃逸的异常会直接终止进程。
            Log($"drop handling failed: {ex}");
            ShowError(ex.Message);
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (FileOpenService.CanAccept(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    public void OpenFileWhenReady(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (VideoPanel.IsLoaded)
        {
            _ = LoadFileAsync(path);
            return;
        }

        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            VideoPanel.Loaded -= loadedHandler;
            _ = LoadFileAsync(path);
        };
        VideoPanel.Loaded += loadedHandler;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                // 非全屏时不吞掉 Esc，留给系统默认行为。
                if (!_fullscreenService.IsFullscreen)
                {
                    return;
                }

                _fullscreenService.ExitFullscreen();
                break;
            case VirtualKey.Space:
                _player.TogglePause();
                break;
            case VirtualKey.Left:
                _player.SeekRelative(-InputService.SeekStepSeconds);
                break;
            case VirtualKey.Right:
                _player.SeekRelative(InputService.SeekStepSeconds);
                break;
            case VirtualKey.Up:
                _player.SetVolume(_lastState.Volume + InputService.VolumeStep);
                break;
            case VirtualKey.Down:
                _player.SetVolume(_lastState.Volume - InputService.VolumeStep);
                break;
            case VirtualKey.M:
                _player.ToggleMute();
                break;
            case VirtualKey.S:
                _player.ToggleSubtitleVisibility();
                break;
            case VirtualKey.A:
                SelectNextTrack(_lastState.AudioTracks, _lastState.SelectedAudioTrackId, _player.SelectAudioTrack);
                break;
            case VirtualKey.V:
                SelectNextTrack(_lastState.SubtitleTracks, _lastState.SelectedSubtitleTrackId, _player.SelectSubtitleTrack);
                break;
            default:
                return;
        }

        ShowOverlay();
        e.Handled = true;
    }

    private void SelectNextTrack(IReadOnlyList<TrackInfo> tracks, int? selectedTrackId, Action<int> select)
    {
        if (!_lastState.HasMedia)
        {
            return;
        }

        if (TrackSelection.GetNextTrackId(tracks, selectedTrackId) is { } nextTrackId)
        {
            select(nextTrackId);
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
        Log("VideoPanel loaded");
        UpdateCompositionSizeFromPanel();
        UpdateOverlayViewportInsets();
    }

    private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 不在这里打日志：UpdateCompositionSizeFromPanel 紧接着就会输出带尺寸的那条，
        // 拖动窗口时每帧两行纯属刷屏。
        UpdateCompositionSizeFromPanel();
        UpdateOverlayViewportInsets();
    }

    private async Task LoadFileAsync(string path, bool isSwapChainRecovery = false)
    {
        if (!isSwapChainRecovery)
        {
            _swapChainRecoveryAttempts = 0;
        }

        var loadGeneration = ++_loadGeneration;
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var previousCancellation = _loadCancellation;
        _loadCancellation = cancellation;
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        _swapChainBinder.Cancel();

        ClearError();
        ShowOverlay();

        try
        {
            UpdateCompositionSizeFromPanel();
            UpdateOverlayViewportInsets();
            await _player.LoadAsync(path, cancellationToken);
            if (loadGeneration != _loadGeneration)
            {
                return;
            }

            UpdateCompositionSizeFromPanel();
            UpdateOverlayViewportInsets();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log($"media load superseded path={path}");
        }
        catch (Exception ex) when (ex is MpvException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (loadGeneration == _loadGeneration)
            {
                ShowError(ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task LoadExternalSubtitleAsync()
    {
        if (!_lastState.HasMedia)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add(".srt");
        picker.FileTypeFilter.Add(".ass");
        picker.FileTypeFilter.Add(".ssa");
        picker.FileTypeFilter.Add(".vtt");

        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            _player.AddSubtitleFile(file.Path);
            ShowOverlay();
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
            Overlay.ApplyState(_lastState, _fullscreenService.IsFullscreen);
            UpdateOverlayViewportInsets();
            UpdateIdleLayer();

            if (!_lastState.HasMedia || _lastState.IsPaused || _lastState.IsEndOfFile)
            {
                ShowOverlay();
            }
        });
    }

    private void OnSwapChainChanged(IntPtr swapChain)
    {
        DispatcherQueue.TryEnqueue(() => _swapChainBinder.Bind(swapChain));
    }

    // async void：这里逃逸的异常会直接终止进程（App 的处理器只记日志、不置 Handled），
    // 所以必须整体兜住。
    private async void OnSwapChainBindCompleted(SwapChainBindOutcome outcome)
    {
        try
        {
            switch (outcome.Status)
            {
                case SwapChainBindStatus.Bound:
                    _swapChainRecoveryAttempts = 0;
                    ClearError();
                    UpdateCompositionSizeFromPanel();
                    UpdateOverlayViewportInsets();
                    return;

                case SwapChainBindStatus.InterfaceUnavailable:
                    ShowError("SwapChainPanel native interop failed: ISwapChainPanelNative not available. Check WinUI 3 dxinterop GUID/interface.");
                    return;
            }

            if (outcome.CanRecoverByReload && await TryRecoverSwapChainBindingAsync())
            {
                return;
            }

            ShowError($"SetSwapChain failed: HRESULT {ComHelpers.FormatHResult(outcome.HResult)}");
        }
        catch (Exception ex)
        {
            Log($"swapchain bind handling failed: {ex}");
            ShowError(ex.Message);
        }
    }

    private async Task<bool> TryRecoverSwapChainBindingAsync()
    {
        var currentFile = _lastState.CurrentFile;
        if (_isRecoveringSwapChain || _swapChainRecoveryAttempts >= 1 || string.IsNullOrWhiteSpace(currentFile))
        {
            return false;
        }

        _swapChainRecoveryAttempts++;
        _isRecoveringSwapChain = true;
        try
        {
            Log("recovering SetSwapChain E_FAIL by reloading current file after panel is ready");
            await LoadFileAsync(currentFile, isSwapChainRecovery: true);
            return true;
        }
        catch (Exception ex) when (ex is MpvException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"SetSwapChain recovery failed: {ex.Message}");
            ShowError(ex.Message);
            return true;
        }
        finally
        {
            _isRecoveringSwapChain = false;
        }
    }

    private void OnFullscreenChanged(bool isFullscreen)
    {
        // 光 SetTitleBar(null) 不够：extend 模式下 WinUI 会回退到默认拖动区
        // （窗口顶部一条），全屏窗口照样能被拖走。必须连 extend 一起关掉——
        // 全屏是 WS_POPUP，本来也没有标题栏可延伸。
        ExtendsContentIntoTitleBar = !isFullscreen;
        SetTitleBar(isFullscreen ? null : Overlay.TitleDragArea);
        Overlay.ApplyState(_lastState, isFullscreen);
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
            Log("d3d11-composition-size skipped: VideoPanel not loaded");
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
            Log($"d3d11-composition-size skipped: invalid panel size dip={VideoPanel.ActualWidth:0.###}x{VideoPanel.ActualHeight:0.###} scale={scale.Value:0.###} pixels={width}x{height}");
            return;
        }

        Log($"d3d11-composition-size panel dip={VideoPanel.ActualWidth:0.###}x{VideoPanel.ActualHeight:0.###} scale={scale.Value:0.###} pixels={width}x{height}");
        _player.SetCompositionSize(width, height);
    }

    private void UpdateOverlayViewportInsets()
    {
        Overlay.SetVideoContentInset(VideoLayout.CalculateHorizontalInset(
            VideoPanel.ActualWidth,
            VideoPanel.ActualHeight,
            _lastState.VideoAspectRatio));
    }

    private void UpdateIdleLayer()
    {
        // 一直盖到 mpv 报告 playback-restart 为止：swapchain 指针可用不代表已经有画面，
        // 提前揭开会闪一下未初始化的 back buffer（首次加载时表现为白屏）。
        var showIdle = !_lastState.HasMedia || !_lastState.IsVideoReady || _hasVisibleError;
        IdleLayer.Visibility = showIdle ? Visibility.Visible : Visibility.Collapsed;

        if (!_lastState.HasMedia)
        {
            IdleTitle.Text = "Drop video here";
            StatusText.Text = "No media";
            return;
        }

        IdleTitle.Text = _hasVisibleError ? "Playback issue" : "Loading";
        StatusText.Text = _lastState.IsSwapChainReady ? "Video surface ready" : "Waiting for D3D11 swapchain";
    }

    // ShowError 会被 player 的后台线程调用，ClearError 目前只来自 UI 线程；
    // 两者都走同一套线程检查，避免哪天调用方换了线程就炸。已在 UI 线程时同步执行，
    // 不引入额外的调度延迟。
    private void ShowError(string message)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowError(message));
            return;
        }

        _hasVisibleError = true;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        UpdateIdleLayer();
        ShowOverlay();
    }

    private void ClearError()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ClearError);
            return;
        }

        _hasVisibleError = false;
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateIdleLayer();
    }

    private void ShowOverlay()
    {
        Overlay.ShowChrome();
        RootGrid.SetCursorVisible(true);
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    private void HideOverlayWhenIdle()
    {
        if (!_lastState.HasMedia || _lastState.IsPaused || _lastState.IsEndOfFile || Overlay.IsPointerWithin || Overlay.IsDragging)
        {
            return;
        }

        Overlay.HideChrome();
        RootGrid.SetCursorVisible(false);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Log("window closing");
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _swapChainBinder.Cancel();
        _swapChainBinder.Clear();

        _player.Dispose();
        Log("window closed");
    }
}
