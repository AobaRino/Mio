using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace Mio.Interop;

public static class SwapChainPanelInterop
{
    private const int EInvalidArg = unchecked((int)0x80070057);
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EFail = unchecked((int)0x80004005);

    public static int SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        if (panel is null)
        {
            return EInvalidArg;
        }

        IntPtr unknown = IntPtr.Zero;
        IntPtr nativePtr = IntPtr.Zero;

        try
        {
            unknown = Marshal.GetIUnknownForObject(panel);

            var iid = typeof(ISwapChainPanelNative).GUID;
            var queryResult = Marshal.QueryInterface(unknown, in iid, out nativePtr);
            Debug.WriteLine($"[Mio.WinUI] QueryInterface ISwapChainPanelNative iid={iid} result={ComHelpers.FormatHResult(queryResult)} ptr=0x{nativePtr.ToInt64():X}");

            if (ComHelpers.Failed(queryResult))
            {
                return queryResult;
            }

            if (nativePtr == IntPtr.Zero)
            {
                Debug.WriteLine("[Mio.WinUI] QueryInterface ISwapChainPanelNative returned success with null pointer");
                return ENoInterface;
            }

            var nativePanel = (ISwapChainPanelNative)Marshal.GetTypedObjectForIUnknown(
                nativePtr,
                typeof(ISwapChainPanelNative));

            var setResult = nativePanel.SetSwapChain(swapChain);
            Debug.WriteLine($"[Mio.WinUI] ISwapChainPanelNative.SetSwapChain ptr=0x{swapChain.ToInt64():X} result={ComHelpers.FormatHResult(setResult)}");
            return setResult;
        }
        catch (InvalidCastException ex)
        {
            Debug.WriteLine($"[Mio.WinUI] ISwapChainPanelNative cast failed: {ex}");
            return ENoInterface;
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Mio.WinUI] ISwapChainPanelNative COM failed: HRESULT {ComHelpers.FormatHResult(ex.HResult)} {ex}");
            return ex.HResult;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Mio.WinUI] SetSwapChain unexpected failure: {ex}");
            return EFail;
        }
        finally
        {
            if (nativePtr != IntPtr.Zero)
            {
                Marshal.Release(nativePtr);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    [ComImport]
    [Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig]
        int SetSwapChain(IntPtr swapChain);
    }
}
