using System.Globalization;
using System.Runtime.InteropServices;

namespace Mio.Interop;

public static class ComHelpers
{
    public static bool Failed(int hresult)
    {
        return hresult < 0;
    }

    public static string FormatHResult(int hresult)
    {
        return "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);
    }

    public static void ThrowIfFailed(int hresult, string operation)
    {
        if (!Failed(hresult))
        {
            return;
        }

        var exception = Marshal.GetExceptionForHR(hresult);
        throw new COMException($"{operation} failed with HRESULT {FormatHResult(hresult)}: {exception?.Message}", hresult);
    }
}
