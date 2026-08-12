using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static Mio.Diagnostics.MioLog;

namespace Mio.Player;

/// <summary>
/// 跑 mpv 的事件循环并收集错误。没有它，解码失败之类的原因只会留在 mpv 内部：
/// loadfile 返回 0 仅代表命令已提交，文件打不开时外面毫无感知。
/// </summary>
internal sealed class MpvEventLoop : IDisposable
{
    private const int MaxRecentLogErrors = 5;
    private const double EventWaitSeconds = 1.0;

    private readonly Func<IntPtr> _getHandle;
    private readonly object _errorSync = new();
    private readonly List<string> _recentLogErrors = new();

    private CancellationTokenSource? _cancellation;
    private long _mediaGeneration;
    private long _loadErrorGeneration = -1;
    private string? _loadErrorMessage;

    /// <param name="getHandle">
    /// 取当前 mpv handle。必须由调用方在自己的锁内读取——handle 会在 Dispose 时被置零，
    /// 事件线程要能观察到这个变化。
    /// </param>
    public MpvEventLoop(Func<IntPtr> getHandle)
    {
        _getHandle = getHandle;
    }

    /// <summary>播放中途失败。此时没有调用方在等待，这是唯一的上报通道。</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>mpv 已就绪并将开始输出画面。</summary>
    public event Action? PlaybackRestarted;

    public Task? Task { get; private set; }

    public void Start()
    {
        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;

        // mpv_wait_event 是阻塞调用，且同一时刻只允许一个线程调用它，
        // 所以固定用一个专属长驻线程，不占线程池。
        Task = System.Threading.Tasks.Task.Factory.StartNew(
            () => Run(cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 请求退出并唤醒可能正阻塞在 mpv_wait_event 的线程。调用方仍需等待
    /// <see cref="Task"/> 结束才能销毁 handle。
    /// </summary>
    public void RequestStop()
    {
        _cancellation?.Cancel();

        var handle = _getHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            MpvNative.Wakeup(handle);
        }
        catch (Exception ex)
        {
            Log($"mpv_wakeup failed: {ex.Message}");
        }
    }

    /// <summary>开始一次新的加载，清空上一个文件残留的错误并返回本次的代号。</summary>
    public long BeginMediaGeneration()
    {
        lock (_errorSync)
        {
            _recentLogErrors.Clear();
            _loadErrorMessage = null;
            return ++_mediaGeneration;
        }
    }

    /// <summary>取本次加载已经确认的失败原因；用代号隔离，避免上一个文件的错误污染这次。</summary>
    public string? TryGetLoadError(long mediaGeneration)
    {
        lock (_errorSync)
        {
            return _loadErrorGeneration == mediaGeneration && _loadErrorMessage is not null
                ? AppendRecentLogErrorsLocked(_loadErrorMessage)
                : null;
        }
    }

    public string AppendRecentLogErrors(string message)
    {
        lock (_errorSync)
        {
            return AppendRecentLogErrorsLocked(message);
        }
    }

    public void Dispose()
    {
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var handle = _getHandle();
            if (handle == IntPtr.Zero)
            {
                return;
            }

            MpvEvent? mpvEvent;
            try
            {
                mpvEvent = MpvNative.WaitEvent(handle, EventWaitSeconds);
            }
            catch (Exception ex)
            {
                Log($"mpv_wait_event failed: {ex.Message}");
                return;
            }

            if (mpvEvent is null)
            {
                continue;
            }

            switch (mpvEvent.Value.EventId)
            {
                case MpvEventId.Shutdown:
                    Log("event shutdown");
                    return;
                case MpvEventId.LogMessage:
                    HandleLogMessage(mpvEvent.Value.Data);
                    break;
                case MpvEventId.EndFile:
                    HandleEndFile(mpvEvent.Value.Data);
                    break;
                case MpvEventId.FileLoaded:
                    Log("event file-loaded");
                    break;
                case MpvEventId.PlaybackRestart:
                    Log("event playback-restart");
                    PlaybackRestarted?.Invoke();
                    break;
            }
        }
    }

    private void HandleLogMessage(IntPtr data)
    {
        var message = MpvNative.ReadLogMessage(data);
        if (message is null)
        {
            return;
        }

        Log($"mpv log: {message}");
        lock (_errorSync)
        {
            if (_recentLogErrors.Count >= MaxRecentLogErrors)
            {
                _recentLogErrors.RemoveAt(0);
            }

            _recentLogErrors.Add(message);
        }
    }

    private void HandleEndFile(IntPtr data)
    {
        var endFile = MpvNative.ReadEndFile(data);
        var reason = (MpvEndFileReason)endFile.Reason;
        Log($"event end-file reason={reason} error={endFile.Error}");

        if (reason != MpvEndFileReason.Error)
        {
            return;
        }

        var message = endFile.Error < 0
            ? $"Playback failed: {MpvNative.ErrorString(endFile.Error)} ({endFile.Error})"
            : "Playback failed: mpv could not open this file.";

        lock (_errorSync)
        {
            _loadErrorGeneration = _mediaGeneration;
            _loadErrorMessage = message;
        }

        ErrorOccurred?.Invoke(AppendRecentLogErrors(message));
    }

    private string AppendRecentLogErrorsLocked(string message)
    {
        return _recentLogErrors.Count == 0
            ? message
            : message + Environment.NewLine + string.Join(Environment.NewLine, _recentLogErrors);
    }
}
