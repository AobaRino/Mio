using System.Globalization;

namespace Mio.Player;

public static class MpvCommand
{
    public static string[] LoadFile(string path)
    {
        return new[] { "loadfile", path, "replace" };
    }

    public static string[] Seek(double seconds, string mode)
    {
        return new[]
        {
            "seek",
            seconds.ToString("0.###", CultureInfo.InvariantCulture),
            $"{mode}+exact"
        };
    }

    public static string[] SubAdd(string path, string flags = "select")
    {
        return new[] { "sub-add", path, flags };
    }
}
