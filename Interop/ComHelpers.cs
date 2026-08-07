using System.Globalization;

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
}
