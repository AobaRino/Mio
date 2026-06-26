using System;
using System.Runtime.InteropServices;

namespace Mio.Interop;

public static partial class NativeMethods
{
    public const int ShowWindowMinimize = 6;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);
}
