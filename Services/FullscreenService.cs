using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Mio.Interop;
using WinRT.Interop;

namespace Mio.Services;

public sealed class FullscreenService
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;

    public FullscreenService(Window window)
    {
        _hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
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
        if (IsFullscreen)
        {
            return;
        }

        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        IsFullscreen = true;
        Log("fullscreen enter");
        FullscreenChanged?.Invoke(true);
    }

    public void ExitFullscreen()
    {
        if (!IsFullscreen)
        {
            return;
        }

        _appWindow.SetPresenter(AppWindowPresenterKind.Default);
        IsFullscreen = false;
        Log("fullscreen exit");
        FullscreenChanged?.Invoke(false);
    }

    public void Minimize()
    {
        NativeMethods.ShowWindow(_hwnd, NativeMethods.ShowWindowMinimize);
    }

    public void ToggleMaximizeRestore()
    {
        if (IsFullscreen)
        {
            ExitFullscreen();
            return;
        }

        if (_appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            presenter.Restore();
        }
        else
        {
            presenter.Maximize();
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[Mio.WinUI] {message}");
    }
}
