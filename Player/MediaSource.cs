using System;
using System.IO;
using System.Linq;

namespace Mio.Player;

public static class MediaSource
{
    /// <summary>
    /// 是否为 mpv 可直接打开的远程地址（http/https/rtsp/rtmp 等）。
    /// Windows 盘符路径和 UNC 路径都会被 Uri 解析成 file scheme，归入本地。
    /// </summary>
    public static bool IsRemote(string source)
    {
        return !string.IsNullOrWhiteSpace(source)
            && Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && !uri.IsFile
            && !uri.IsUnc;
    }

    /// <summary>
    /// 交给 mpv 的字符串：远程地址原样保留，本地路径规范化为完整路径。
    /// </summary>
    public static string Normalize(string source)
    {
        return IsRemote(source) ? source : Path.GetFullPath(source);
    }

    /// <summary>
    /// 载入完成前的临时标题。mpv 解析出 media-title 后会覆盖它。
    /// </summary>
    public static string GetDisplayName(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Mio";
        }

        if (!IsRemote(source))
        {
            var fileName = Path.GetFileName(source);
            return string.IsNullOrWhiteSpace(fileName) ? source : fileName;
        }

        var uri = new Uri(source);
        var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(lastSegment)
            ? uri.Host
            : Uri.UnescapeDataString(lastSegment);
    }
}
