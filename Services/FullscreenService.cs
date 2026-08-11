using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mio.Interop;
using Windows.Graphics;
using WinRT.Interop;
using static Mio.Diagnostics.MioLog;

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
    private long _styleBeforeFullscreen;

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

        // 不走 presenter 的 SetBorderAndTitleBar / IsResizable：它们的效果已被
        // WS_POPUP 完全覆盖，多调一次只是多一帧「旧尺寸 + 新样式」的重绘，
        // 表现为切换全屏时边框一闪。样式和尺寸合并成一次 SetWindowPos 生效。
        _styleBeforeFullscreen = NativeMethods.ApplyBorderlessFullscreenStyle(_hwnd);
        SetDwmBorderVisible(false);
        NativeMethods.SetWindowBounds(_hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        IsFullscreen = true;
        Log($"fullscreen enter (restore to {_stateBeforeFullscreen}) display={bounds.Width}x{bounds.Height} " +
            $"window={_appWindow.Size.Width}x{_appWindow.Size.Height} pos={_appWindow.Position.X},{_appWindow.Position.Y} " +
            $"style=0x{_styleBeforeFullscreen:X8}->0x{NativeMethods.GetWindowStyle(_hwnd):X8}");
        FullscreenChanged?.Invoke(true);
    }

    public void ExitFullscreen()
    {
        if (!IsFullscreen || _appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        NativeMethods.SetWindowStyle(_hwnd, _styleBeforeFullscreen);
        SetDwmBorderVisible(true);

        // 还原样式后必须 FRAMECHANGED 才会生效，两条分支都要覆盖到。
        if (_stateBeforeFullscreen == OverlappedPresenterState.Maximized)
        {
            NativeMethods.ApplyFrameChange(_hwnd);
            presenter.Maximize();
        }
        else if (_boundsBeforeFullscreen.Width > 0 && _boundsBeforeFullscreen.Height > 0)
        {
            NativeMethods.SetWindowBounds(
                _hwnd,
                _boundsBeforeFullscreen.X,
                _boundsBeforeFullscreen.Y,
                _boundsBeforeFullscreen.Width,
                _boundsBeforeFullscreen.Height);
        }
        else
        {
            NativeMethods.ApplyFrameChange(_hwnd);
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
}
