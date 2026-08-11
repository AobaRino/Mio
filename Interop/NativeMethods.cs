using System;
using System.Runtime.InteropServices;

namespace Mio.Interop;

public static partial class NativeMethods
{
    // DWMWA_BORDER_COLOR，Windows 11 22000+。设成 DwmBorderColorNone 可以让
    // DWM 不画窗口外那条边框线；旧系统上调用只会返回错误，无副作用。
    public const int DwmwaBorderColor = 34;
    public const uint DwmBorderColorNone = 0xFFFFFFFE;
    public const uint DwmBorderColorDefault = 0xFFFFFFFF;

    private const int GwlStyle = -16;
    private const long WsPopup = 0x80000000L;
    private const long WsOverlappedWindow = 0x00CF0000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const int GclpHbrBackground = -10;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // 改过窗口样式后必须让系统重算非客户区，否则客户区会沿用旧的边框宽度。
    public static void ApplyFrameChange(IntPtr hwnd)
    {
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    // 样式重算和新位置尺寸合并成一次调用。分两步做的话，中间会有一帧是
    // 「旧尺寸 + 新样式」，切换全屏时表现为边框一闪。
    public static void SetWindowBounds(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    // 窗口尺寸变化时，XAML 布局要一帧才铺满新区域；这一帧里露出的是窗口类的
    // 默认背景刷（系统默认白色），表现为切换全屏时闪一下白边。把类背景刷换成
    // 黑色，露出的就与播放器底色一致，肉眼看不出来。colorRef 是 0x00BBGGRR。
    public static void SetWindowBackgroundColor(IntPtr hwnd, uint colorRef)
    {
        var brush = CreateSolidBrush(colorRef);
        if (brush != IntPtr.Zero)
        {
            SetClassLongPtr(hwnd, GclpHbrBackground, brush);
        }
    }

    public static long GetWindowStyle(IntPtr hwnd)
    {
        return GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
    }

    public static void SetWindowStyle(IntPtr hwnd, long style)
    {
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
    }

    // 全屏直接把窗口换成 WS_POPUP：它完全没有非客户区，不会残留边框或标题栏白边，
    // Windows 也据此把窗口识别成全屏应用并让出任务栏。只清 WS_CAPTION/WS_THICKFRAME
    // 是不够的——窗口仍被当作普通层叠窗口，任务栏不会让位。
    // OverlappedPresenter 的 SetBorderAndTitleBar / IsResizable 在不同
    // WindowsAppSDK 版本下行为不一致（1.5 与 2.3 就不同），所以不依赖它们。
    // 返回改动前的样式供退出时还原。
    public static long ApplyBorderlessFullscreenStyle(IntPtr hwnd)
    {
        var style = GetWindowStyle(hwnd);
        SetWindowStyle(hwnd, (style & ~WsOverlappedWindow) | WsPopup);
        return style;
    }
}
