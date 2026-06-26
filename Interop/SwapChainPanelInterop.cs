using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Mio.Interop;

public static class SwapChainPanelInterop
{
    public static int SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        var nativePanel = panel.As<ISwapChainPanelNative>();
        return nativePanel.SetSwapChain(swapChain);
    }

    [ComImport]
    [Guid("F92F19D2-3ADE-45A6-A20C-F6F1EA90554B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }
}
