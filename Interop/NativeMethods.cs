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

    // 改过窗口样式后必须让系统重算非客户区，否则客户区会沿用旧的边框宽度。
    public static void ApplyFrameChange(IntPtr hwnd)
    {
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }
}
