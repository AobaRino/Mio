using System.Diagnostics;

namespace Mio.Diagnostics;

/// <summary>
/// 统一的调试日志前缀。用 <c>using static Mio.Diagnostics.MioLog;</c> 导入后直接调用 Log()。
/// 注意 Debug.WriteLine 带 [Conditional("DEBUG")]，Release 构建下这些调用会被整体移除。
/// </summary>
public static class MioLog
{
    public static void Log(string message)
    {
        Debug.WriteLine($"[Mio.WinUI] {message}");
    }
}
