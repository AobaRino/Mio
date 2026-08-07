using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mio.Interop;
using Windows.Graphics;
using WinRT.Interop;

namespace Mio.Services;

/// <summary>
/// 全屏用「同一个 OverlappedPresenter 去边框 + 铺满显示器」实现，而不是切换到
/// AppWindowPresenterKind.FullScreen。切换 presenter kind 会让窗口先卸回 Restored
/// 再套用新 presenter，从最大化进出全屏时那一步中间态是肉眼可见的。
/// </summary>
public sealed class FullscreenService
{
    private readonly AppWindow _appWindow;
    private readonly WindowId _windowId;
    private readonly IntPtr _hwnd;

    private OverlappedPresenterState _stateBeforeFullscreen = OverlappedPresenterState.Restored;
    private RectInt32 _boundsBeforeFullscreen;
    private bool _wasResizableBeforeFullscreen = true;

    public FullscreenService(Window window)
    {
        _hwnd = WindowNative.GetWindowHandle(window);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);
    }

    public event Action<bool>? FullscreenChanged;

    public bool IsFullscreen { get; private set; }

    public void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    public void EnterFullscreen()
    {
        if (IsFullscreen || _appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        _stateBeforeFullscreen = presenter.State;
        if (presenter.State != OverlappedPresenterState.Maximized)
        {
            var position = _appWindow.Position;
            var size = _appWindow.Size;
            _boundsBeforeFullscreen = new RectInt32(position.X, position.Y, size.Width, size.Height);
        }

        // OuterBounds 含任务栏区域，WorkArea 不含；全屏要盖住任务栏所以用前者。
        var displayArea = DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Nearest);
        var bounds = displayArea.OuterBounds;

        // 必须先关掉 IsResizable：可调整大小的窗口一定保留 WS_THICKFRAME，
        // 那条 resize border 会占掉客户区（实测每边 7px），视频就铺不满屏。
        _wasResizableBeforeFullscreen = presenter.IsResizable;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        NativeMethods.ApplyFrameChange(_hwnd);
        SetDwmBorderVisible(false);
        _appWindow.MoveAndResize(bounds);

        IsFullscreen = true;
        Log($"fullscreen enter (restore to {_stateBeforeFullscreen}) display={bounds.Width}x{bounds.Height} " +
            $"window={_appWindow.Size.Width}x{_appWindow.Size.Height} pos={_appWindow.Position.X},{_appWindow.Position.Y}");
        FullscreenChanged?.Invoke(true);
    }

    public void ExitFullscreen()
    {
        if (!IsFullscreen || _appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        presenter.SetBorderAndTitleBar(true, true);
        presenter.IsResizable = _wasResizableBeforeFullscreen;
        NativeMethods.ApplyFrameChange(_hwnd);
        SetDwmBorderVisible(true);

        if (_stateBeforeFullscreen == OverlappedPresenterState.Maximized)
        {
            presenter.Maximize();
        }
        else if (_boundsBeforeFullscreen.Width > 0 && _boundsBeforeFullscreen.Height > 0)
        {
            _appWindow.MoveAndResize(_boundsBeforeFullscreen);
        }

        IsFullscreen = false;
        Log("fullscreen exit");
        FullscreenChanged?.Invoke(false);
    }

    private void SetDwmBorderVisible(bool visible)
    {
        var color = visible ? NativeMethods.DwmBorderColorDefault : NativeMethods.DwmBorderColorNone;
        var hr = NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DwmwaBorderColor, ref color, sizeof(uint));
        Log($"dwm border={(visible ? "default" : "none")} result=0x{hr:X8}");
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[Mio.WinUI] {message}");
    }
}
