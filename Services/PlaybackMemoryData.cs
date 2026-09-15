using System;
using System.Collections.Generic;
using System.Linq;

namespace Mio.Services;

public sealed class ResumeEntry
{
    public double Position { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 跨会话记忆的数据与淘汰规则。纯逻辑、可序列化，不碰文件系统，便于测试；
/// 读写磁盘由 <see cref="PlaybackMemory"/> 负责。
/// </summary>
public sealed class PlaybackMemoryData
{
    public const int MaxResumeEntries = 200;

    // 看到这个比例之后视为看完，下次从头放而不是停在片尾字幕上。
    public const double CompletionThreshold = 0.95;

    // 刚开始的几秒不值得记，否则每个只点开看了一眼的文件都会留一条。
    public const double MinimumResumeSeconds = 10;

    public double Volume { get; set; } = 100;

    // Windows 路径不区分大小写；URL 理论上区分，但实际几乎不会遇到仅大小写不同的两个地址。
    public Dictionary<string, ResumeEntry> Resume { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double? GetResumePosition(string source)
    {
        return Resume.TryGetValue(source, out var entry) ? entry.Position : null;
    }

    /// <summary>更新某个来源的续播位置。返回数据是否发生了变化。</summary>
    public bool RememberPosition(string source, double position, double duration, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(source) || duration <= 0)
        {
            return false;
        }

        if (position < MinimumResumeSeconds || position / duration >= CompletionThreshold)
        {
            return Resume.Remove(source);
        }

        if (Resume.TryGetValue(source, out var existing) && Math.Abs(existing.Position - position) < 0.5)
        {
            return false;
        }

        Resume[source] = new ResumeEntry { Position = position, UpdatedAt = now };
        Trim();
        return true;
    }

    public bool Forget(string source)
    {
        return Resume.Remove(source);
    }

    private void Trim()
    {
        var excess = Resume.Count - MaxResumeEntries;
        if (excess <= 0)
        {
            return;
        }

        var oldest = Resume
            .OrderBy(pair => pair.Value.UpdatedAt)
            .Take(excess)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in oldest)
        {
            Resume.Remove(key);
        }
    }
}
