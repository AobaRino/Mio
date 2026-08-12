using System;
using System.Globalization;

namespace Mio.Player;

public static class TimeFormat
{
    /// <summary>
    /// 播放时间显示。不足一小时用 m:ss，超过则用 h:mm:ss；无效值（负数、NaN、
    /// 无穷大）一律回落到 0:00，因为时长未知时 mpv 会返回这些值。
    /// </summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            return "0:00";
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)time.TotalMinutes, time.Seconds);
    }
}
