using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using static Mio.Diagnostics.MioLog;

namespace Mio.Services;

/// <summary>
/// 把 <see cref="PlaybackMemoryData"/> 落到 %LOCALAPPDATA%\Mio\playback.json。
/// 内存里随时更新，磁盘按固定间隔加退出时写，避免播放中每 300ms 刷一次盘。
/// </summary>
public sealed class PlaybackMemory : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly DispatcherQueueTimer _flushTimer;
    private readonly PlaybackMemoryData _data;
    private bool _dirty;

    public PlaybackMemory(DispatcherQueue dispatcherQueue)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mio");
        _path = Path.Combine(directory, "playback.json");
        _data = Load(_path);

        _flushTimer = dispatcherQueue.CreateTimer();
        _flushTimer.Interval = FlushInterval;
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public double Volume => _data.Volume;

    public void RememberVolume(double volume)
    {
        if (Math.Abs(_data.Volume - volume) < 0.5)
        {
            return;
        }

        _data.Volume = volume;
        _dirty = true;
    }

    public double? GetResumePosition(string source)
    {
        return _data.GetResumePosition(source);
    }

    public void RememberPosition(string source, double position, double duration)
    {
        if (_data.RememberPosition(source, position, duration, DateTimeOffset.UtcNow))
        {
            _dirty = true;
        }
    }

    public void Flush()
    {
        if (!_dirty)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记忆丢了不影响播放，下次 flush 再试。
            Log($"playback memory save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        Flush();
    }

    private static PlaybackMemoryData Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new PlaybackMemoryData();
            }

            var data = JsonSerializer.Deserialize<PlaybackMemoryData>(File.ReadAllText(path));
            if (data is null)
            {
                return new PlaybackMemoryData();
            }

            // 反序列化出来的字典默认是区分大小写的，换回不区分的比较器。
            data.Resume = new(data.Resume, StringComparer.OrdinalIgnoreCase);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 文件损坏就当没有，别让一个坏 JSON 挡住启动。
            Log($"playback memory load failed, starting fresh: {ex.Message}");
            return new PlaybackMemoryData();
        }
    }
}
