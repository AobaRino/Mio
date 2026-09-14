using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Mio.Diagnostics.MioLog;

namespace Mio.Player;

public sealed class MpvPlayer : IMediaPlayer
{
    private static readonly (string Name, string Value)[] RequiredOptions =
    {
        ("config", "no"),
        ("vo", "gpu-next"),
        ("gpu-api", "d3d11"),
        ("gpu-context", "d3d11"),
        ("d3d11-output-mode", "composition"),
        ("hwdec", "d3d11va"),
        ("target-colorspace-hint", "auto"),
        ("input-default-bindings", "no"),
        ("input-vo-keyboard", "no"),
        ("keep-open", "yes")
    };

    private const int MaxTrackCount = 512;
    private static readonly TimeSpan BackgroundShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SwapChainWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SwapChainPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SwapChainDiagnosticInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly MpvEventLoop _eventLoop;
    private IReadOnlyList<TrackInfo> _cachedAudioTracks = Array.Empty<TrackInfo>();
    private IReadOnlyList<TrackInfo> _cachedSubtitleTracks = Array.Empty<TrackInfo>();
    private int _lastTrackListCount = -1;
    private IntPtr _handle;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private PlayerState _state = PlayerState.CreateIdle();
    private string? _currentFile;
    private IntPtr _lastSwapChain;
    private int _lastCompositionWidth;
    private int _lastCompositionHeight;
    private bool _videoReady;
    private bool _initialized;
    private bool _disposed;

    public event Action<PlayerState>? StateChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<IntPtr>? SwapChainChanged;

    public MpvPlayer()
    {
        _eventLoop = new MpvEventLoop(GetHandle);
        _eventLoop.ErrorOccurred += message => ErrorOccurred?.Invoke(message);
        _eventLoop.PlaybackRestarted += OnPlaybackRestarted;
    }

    private IntPtr GetHandle()
    {
        lock (_sync)
        {
            return _handle;
        }
    }

    public PlayerState State
    {
        get
        {
            lock (_sync)
            {
                return _state.Clone();
            }
        }
    }

    public void Initialize()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_initialized)
            {
                return;
            }

            _handle = MpvNative.Create();
            if (_handle == IntPtr.Zero)
            {
                throw new MpvException("mpv_create failed: libmpv returned a null handle.");
            }

            Log("libmpv loaded");
            Log("mpv_create success");

            foreach (var option in RequiredOptions)
            {
                SetOptionLocked(option.Name, option.Value);
            }

            var initializeResult = MpvNative.Initialize(_handle);
            Log($"mpv_initialize result={DescribeResult(initializeResult)}");
            MpvNative.ThrowIfError(initializeResult, "mpv_initialize");

            // 没有这个，解码失败之类的原因只会留在 mpv 内部，外面看不到。
            var logResult = MpvNative.RequestLogMessages(_handle, "error");
            Log($"mpv_request_log_messages(error) result={DescribeResult(logResult)}");

            _initialized = true;
        }

        StartPolling();
        _eventLoop.Start();
    }

    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        // 远程地址交给 mpv 自己解析；只有本地路径才做存在性检查。
        var source = MediaSource.Normalize(path);
        if (!MediaSource.IsRemote(source) && !File.Exists(source))
        {
            throw new FileNotFoundException("Media file was not found.", source);
        }

        var mediaGeneration = _eventLoop.BeginMediaGeneration();

        PlayerState snapshot;
        lock (_sync)
        {
            Log($"loadfile path={source}");
            var result = MpvNative.Command(_handle, MpvCommand.LoadFile(source));
            Log($"loadfile result={DescribeResult(result)}");
            MpvNative.ThrowIfError(result, "loadfile");

            _currentFile = source;
            _lastSwapChain = IntPtr.Zero;
            _videoReady = false;
            ResetTrackCacheLocked();
            _state = _state.CloneForNewMedia(source, MediaSource.GetDisplayName(source));
            snapshot = _state.Clone();
        }

        StateChanged?.Invoke(snapshot);
        await WaitForDisplaySwapChainAsync(mediaGeneration, cancellationToken);
    }

    public void TogglePause()
    {
        var state = State;
        if (state.IsEndOfFile)
        {
            TryCommand(MpvCommand.Seek(0, "absolute"), "restart seek result");
            TrySetProperty(MpvProperty.Pause, "no", "restart pause result");
            return;
        }

        TrySetProperty(MpvProperty.Pause, state.IsPaused ? "no" : "yes", "pause result");
    }

    public void SeekRelative(double seconds)
    {
        var state = State;
        if (!state.HasMedia || state.Duration <= 0 || state.IsIdleActive)
        {
            Log($"seek ignored: hasMedia={state.HasMedia} duration={state.Duration:0.###} idleActive={state.IsIdleActive}");
            return;
        }

        TryCommand(MpvCommand.Seek(seconds, "relative"), "seek result");
    }

    public void SeekAbsolute(double seconds)
    {
        var state = State;
        if (!state.HasMedia || state.Duration <= 0 || state.IsIdleActive)
        {
            Log($"seek ignored: hasMedia={state.HasMedia} duration={state.Duration:0.###} idleActive={state.IsIdleActive}");
            return;
        }

        var target = Math.Min(state.Duration, Math.Max(0, seconds));
        TryCommand(MpvCommand.Seek(target, "absolute"), "seek result");
        if (state.IsEndOfFile && target < state.Duration)
        {
            TrySetProperty(MpvProperty.Pause, "no", "resume after seek result");
        }
    }

    public void SetVolume(double volume)
    {
        var clamped = Math.Min(100, Math.Max(0, volume));
        TrySetProperty(MpvProperty.Volume, clamped.ToString("0.###", CultureInfo.InvariantCulture), "volume result");
    }

    public void ToggleMute()
    {
        var state = State;
        if (!state.HasMedia)
        {
            return;
        }

        TrySetProperty(MpvProperty.Mute, state.IsMuted ? "no" : "yes", "toggle mute");
        PublishStateSnapshot();
    }

    public void SelectAudioTrack(int trackId)
    {
        var state = State;
        if (!state.HasMedia || !state.AudioTracks.Any(track => track.Id == trackId))
        {
            Log($"select audio track ignored: id={trackId}");
            return;
        }

        TrySetProperty(MpvProperty.Aid, trackId.ToString(CultureInfo.InvariantCulture), "select audio track");
        PublishStateSnapshot();
    }

    public void SelectSubtitleTrack(int trackId)
    {
        var state = State;
        if (!state.HasMedia || !state.SubtitleTracks.Any(track => track.Id == trackId))
        {
            Log($"select subtitle track ignored: id={trackId}");
            return;
        }

        TrySetProperty(MpvProperty.Sid, trackId.ToString(CultureInfo.InvariantCulture), "select subtitle track");
        TrySetProperty(MpvProperty.SubVisibility, "yes", "show subtitles");
        PublishStateSnapshot();
    }

    public void DisableSubtitles()
    {
        if (!State.HasMedia)
        {
            return;
        }

        TrySetProperty(MpvProperty.Sid, "no", "disable subtitles");
        PublishStateSnapshot();
    }

    public void AutoSelectSubtitles()
    {
        if (!State.HasMedia)
        {
            return;
        }

        TrySetProperty(MpvProperty.Sid, "auto", "auto subtitles");
        TrySetProperty(MpvProperty.SubVisibility, "yes", "show subtitles");
        PublishStateSnapshot();
    }

    public void ToggleSubtitleVisibility()
    {
        var state = State;
        if (!state.HasMedia)
        {
            return;
        }

        TrySetProperty(MpvProperty.SubVisibility, state.SubtitlesVisible ? "no" : "yes", "toggle subtitle visibility");
        PublishStateSnapshot();
    }

    public void AddSubtitleFile(string path)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Subtitle file was not found.", fullPath);
        }

        int result;
        lock (_sync)
        {
            if (!_state.HasMedia || _state.IsIdleActive)
            {
                Log($"sub-add ignored: no media path={fullPath}");
                return;
            }

            result = MpvNative.Command(_handle, MpvCommand.SubAdd(fullPath));
            Log($"sub-add result={DescribeResult(result)} path={fullPath}");
        }

        MpvNative.ThrowIfError(result, "sub-add");
        PublishStateSnapshot(forceTrackReload: true);
    }

    public void SetCompositionSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            Log($"d3d11-composition-size ignored: invalid size {width}x{height}");
            return;
        }

        lock (_sync)
        {
            if (!_initialized || _handle == IntPtr.Zero)
            {
                Log("d3d11-composition-size ignored: mpv not initialized");
                return;
            }

            if (_lastCompositionWidth == width && _lastCompositionHeight == height)
            {
                return;
            }

            var value = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
            var result = MpvNative.SetPropertyString(_handle, MpvProperty.D3D11CompositionSize, value);
            Log($"d3d11-composition-size set {value} result={DescribeResult(result)}");
            if (result < 0)
            {
                Log($"d3d11-composition-size failed: {MpvNative.ErrorString(result)} ({result})");
                return;
            }

            _lastCompositionWidth = width;
            _lastCompositionHeight = height;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log("dispose start");
        _pollCancellation?.Cancel();

        // 内部会唤醒可能正阻塞在 mpv_wait_event 的线程，让它尽快看到取消信号。
        _eventLoop.RequestStop();

        // 事件线程在锁外调用 mpv_wait_event，必须确认后台任务都已退出才能销毁 handle。
        // 等不到就宁可泄漏 handle：进程即将退出，泄漏无害，use-after-free 会崩。
        if (!WaitForBackgroundTasks())
        {
            Log("background tasks still running; skipping mpv_terminate_destroy to avoid use-after-free");
            _pollCancellation?.Dispose();
            _eventLoop.Dispose();
            return;
        }

        IntPtr handle;
        lock (_sync)
        {
            handle = _handle;
            _handle = IntPtr.Zero;
            _initialized = false;
        }

        if (handle != IntPtr.Zero)
        {
            try
            {
                MpvNative.TerminateDestroy(handle);
            }
            catch (Exception ex)
            {
                Log($"mpv_terminate_destroy failed: {ex.Message}");
            }
        }

        _pollCancellation?.Dispose();
        _eventLoop.Dispose();
        Log("dispose complete");
    }

    private bool WaitForBackgroundTasks()
    {
        var tasks = new List<Task>(2);
        if (_eventLoop.Task is not null)
        {
            tasks.Add(_eventLoop.Task);
        }

        if (_pollTask is not null)
        {
            tasks.Add(_pollTask);
        }

        if (tasks.Count == 0)
        {
            return true;
        }

        try
        {
            return Task.WaitAll(tasks.ToArray(), BackgroundShutdownTimeout);
        }
        catch (AggregateException ex)
        {
            // 任务已经结束，只是带着异常；handle 此刻不再被使用，销毁是安全的。
            Log($"background task faulted during shutdown: {ex.InnerException?.Message ?? ex.Message}");
            return true;
        }
    }

    private void StartPolling()
    {
        _pollCancellation = new CancellationTokenSource();
        _pollTask = PollStateAsync(_pollCancellation.Token);
    }

    // playback-restart 表示 mpv 已经就绪并会开始输出画面。在此之前 swapchain 虽然
    // 已创建，back buffer 里却还是未初始化内容，直接显示会闪一下白屏。
    private void OnPlaybackRestarted()
    {
        lock (_sync)
        {
            if (_videoReady)
            {
                return;
            }

            _videoReady = true;
        }

        PublishStateSnapshot();
    }

    private async Task WaitForDisplaySwapChainAsync(long mediaGeneration, CancellationToken cancellationToken)
    {
        Log("waiting display-swapchain");
        var deadline = DateTimeOffset.UtcNow.Add(SwapChainWaitTimeout);
        var nextDiagnostic = DateTimeOffset.MinValue;
        var diagnostics = new DisplaySwapChainDiagnostics();

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // mpv 已经明确报告这次加载失败，再等 swapchain 只会把"文件打不开"
            // 误报成 D3D11 配置问题。
            var loadError = _eventLoop.TryGetLoadError(mediaGeneration);
            if (loadError is not null)
            {
                throw new MpvException(loadError);
            }

            // 循环本身只需要 display-swapchain 一个属性；那一整套诊断字段只喂给
            // 限流后的日志，按诊断节奏读就够，不必每轮都读。
            var wantDiagnostic = DateTimeOffset.UtcNow >= nextDiagnostic;

            IntPtr swapChain;
            PlayerState snapshot;
            var changed = false;

            lock (_sync)
            {
                if (wantDiagnostic)
                {
                    diagnostics = ReadDiagnosticsLocked();
                    swapChain = diagnostics.SwapChainPointer;
                }
                else
                {
                    swapChain = ReadDisplaySwapChainPointerLocked();
                }

                if (swapChain != IntPtr.Zero && swapChain != _lastSwapChain)
                {
                    _lastSwapChain = swapChain;
                    var next = _state.Clone();
                    next.IsSwapChainReady = true;
                    _state = next;
                    changed = true;
                }

                snapshot = _state.Clone();
            }

            if (swapChain != IntPtr.Zero)
            {
                Log($"display-swapchain ready ptr=0x{swapChain.ToInt64():X}");
                if (changed)
                {
                    StateChanged?.Invoke(snapshot);
                    SwapChainChanged?.Invoke(swapChain);
                }

                return;
            }

            if (wantDiagnostic)
            {
                Log(diagnostics.ToLogLine());
                nextDiagnostic = DateTimeOffset.UtcNow.Add(SwapChainDiagnosticInterval);
            }

            await Task.Delay(SwapChainPollInterval, cancellationToken).ConfigureAwait(false);
        }

        var timeoutLoadError = _eventLoop.TryGetLoadError(mediaGeneration);
        if (timeoutLoadError is not null)
        {
            throw new MpvException(timeoutLoadError);
        }

        lock (_sync)
        {
            diagnostics = ReadDiagnosticsLocked();
        }

        // 只抛出，由 LoadAsync 的调用方统一上报；ErrorOccurred 留给没有调用方
        // 在等待的异步错误，避免同一条错误走两个通道。
        throw new MpvException(_eventLoop.AppendRecentLogErrors(diagnostics.ToTimeoutMessage()));
    }

    private IntPtr ReadDisplaySwapChainPointerLocked()
    {
        if (!_initialized || _handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return MpvNative.TryGetInt64(_handle, MpvProperty.DisplaySwapChain, out var raw) && raw != 0
            ? new IntPtr(raw)
            : IntPtr.Zero;
    }

    private DisplaySwapChainDiagnostics ReadDiagnosticsLocked()
    {
        return DisplaySwapChainDiagnostics.Read(
            _initialized ? _handle : IntPtr.Zero,
            _currentFile,
            _lastCompositionWidth,
            _lastCompositionHeight);
    }

    private async Task PollStateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StatePollInterval, cancellationToken).ConfigureAwait(false);
                PlayerState snapshot;
                lock (_sync)
                {
                    if (!_initialized || _handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    snapshot = PollStateLocked();
                    _state = snapshot.Clone();
                }

                StateChanged?.Invoke(snapshot);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"state poll failed: {ex.Message}");
            }
        }
    }

    private PlayerState PollStateLocked(bool forceTrackReload = false)
    {
        var next = _state.Clone();

        if (MpvNative.TryGetFlag(_handle, MpvProperty.Pause, out var paused))
        {
            next.IsPaused = paused;
        }

        if (MpvNative.TryGetDouble(_handle, MpvProperty.TimePosition, out var position))
        {
            next.Position = Math.Max(0, position);
        }

        if (MpvNative.TryGetDouble(_handle, MpvProperty.Duration, out var duration))
        {
            next.Duration = Math.Max(0, duration);
        }

        if (MpvNative.TryGetInt64(_handle, MpvProperty.VideoWidth, out var videoWidth))
        {
            next.VideoWidth = videoWidth > 0 && videoWidth <= int.MaxValue ? (int)videoWidth : 0;
        }

        if (MpvNative.TryGetInt64(_handle, MpvProperty.VideoHeight, out var videoHeight))
        {
            next.VideoHeight = videoHeight > 0 && videoHeight <= int.MaxValue ? (int)videoHeight : 0;
        }

        if (MpvNative.TryGetDouble(_handle, MpvProperty.VideoAspect, out var videoAspect))
        {
            next.VideoAspect = videoAspect > 0 && !double.IsInfinity(videoAspect) && !double.IsNaN(videoAspect)
                ? videoAspect
                : 0;
        }

        if (MpvNative.TryGetInt64(_handle, MpvProperty.DisplayWidth, out var displayWidth))
        {
            next.DisplayWidth = displayWidth > 0 && displayWidth <= int.MaxValue ? (int)displayWidth : 0;
        }

        if (MpvNative.TryGetInt64(_handle, MpvProperty.DisplayHeight, out var displayHeight))
        {
            next.DisplayHeight = displayHeight > 0 && displayHeight <= int.MaxValue ? (int)displayHeight : 0;
        }

        if (MpvNative.TryGetDouble(_handle, MpvProperty.Volume, out var volume))
        {
            next.Volume = Math.Min(100, Math.Max(0, volume));
        }

        if (MpvNative.TryGetFlag(_handle, MpvProperty.Mute, out var muted))
        {
            next.IsMuted = muted;
        }

        if (MpvNative.TryGetFlag(_handle, MpvProperty.IdleActive, out var idleActive))
        {
            next.IsIdleActive = idleActive;
        }

        if (MpvNative.TryGetFlag(_handle, MpvProperty.EofReached, out var eofReached))
        {
            next.IsEndOfFile = eofReached;
        }

        next.CurrentFile = _currentFile;
        next.MediaTitle = MpvNative.GetPropertyString(_handle, MpvProperty.MediaTitle)
            ?? (_currentFile is null ? "Mio" : MediaSource.GetDisplayName(_currentFile));
        next.HasMedia = !next.IsIdleActive && !string.IsNullOrWhiteSpace(_currentFile);
        next.IsSwapChainReady = _lastSwapChain != IntPtr.Zero;
        next.IsVideoReady = _videoReady;
        if (next.HasMedia)
        {
            RefreshTrackListsLocked(forceTrackReload);
            next.AudioTracks = _cachedAudioTracks;
            next.SubtitleTracks = _cachedSubtitleTracks;
            next.SelectedAudioTrackId = ReadCurrentTrackIdLocked(MpvProperty.CurrentAudioTrackId);
            next.SelectedSubtitleTrackId = ReadCurrentTrackIdLocked(MpvProperty.CurrentSubtitleTrackId);
            if (MpvNative.TryGetFlag(_handle, MpvProperty.SubVisibility, out var subtitlesVisible))
            {
                next.SubtitlesVisible = subtitlesVisible;
            }
        }
        else
        {
            ResetTrackCacheLocked();
            next.AudioTracks = Array.Empty<TrackInfo>();
            next.SubtitleTracks = Array.Empty<TrackInfo>();
            next.SelectedAudioTrackId = null;
            next.SelectedSubtitleTrackId = null;
            next.SubtitlesVisible = false;
        }

        return next;
    }

    private int? ReadCurrentTrackIdLocked(string property)
    {
        // 轨道未选中时 mpv 直接让 current-tracks/<type> 不可用，读取失败即代表 null。
        return MpvNative.TryGetInt64(_handle, property, out var id) && id >= 0 && id <= int.MaxValue
            ? (int)id
            : null;
    }

    private void ResetTrackCacheLocked()
    {
        _lastTrackListCount = -1;
        _cachedAudioTracks = Array.Empty<TrackInfo>();
        _cachedSubtitleTracks = Array.Empty<TrackInfo>();
    }

    // 全量读取 track-list 每条轨道要 ~8 次 P/Invoke，而轨道集合在播放期间几乎不变。
    // 稳态下只比对 track-list/count，变化时才重建；轨道增删之外的操作（切换选中轨道）
    // 由 current-tracks/<type>/id 反映，不需要重读整个列表。
    private void RefreshTrackListsLocked(bool force)
    {
        var count = MpvNative.TryGetInt64(_handle, MpvProperty.TrackListCount, out var rawCount)
            ? (int)Math.Clamp(rawCount, 0, MaxTrackCount)
            : 0;

        if (!force && count == _lastTrackListCount)
        {
            return;
        }

        var audioTracks = new List<TrackInfo>();
        var subtitleTracks = new List<TrackInfo>();

        for (var index = 0; index < count; index++)
        {
            var typeValue = GetTrackString(index, "type");
            var type = typeValue switch
            {
                "audio" => MediaTrackType.Audio,
                "sub" => MediaTrackType.Subtitle,
                _ => (MediaTrackType?)null
            };

            if (type is null)
            {
                continue;
            }

            if (!MpvNative.TryGetInt64(_handle, MpvProperty.TrackListProperty(index, "id"), out var rawId))
            {
                continue;
            }

            var id = (int)Math.Clamp(rawId, int.MinValue, int.MaxValue);
            var title = GetTrackString(index, "title");
            var language = GetTrackString(index, "lang");
            var codec = GetTrackString(index, "codec");
            var isExternal = GetTrackFlag(index, "external");
            var isDefault = GetTrackFlag(index, "default");
            var isForced = GetTrackFlag(index, "forced");
            var track = new TrackInfo
            {
                Type = type.Value,
                Id = id,
                Title = title,
                Language = language,
                Codec = codec,
                IsExternal = isExternal,
                IsDefault = isDefault,
                IsForced = isForced,
                DisplayName = TrackInfo.BuildDisplayName(type.Value, id, title, language, codec, isExternal, isDefault, isForced)
            };

            if (type == MediaTrackType.Audio)
            {
                audioTracks.Add(track);
            }
            else
            {
                subtitleTracks.Add(track);
            }
        }

        _cachedAudioTracks = audioTracks;
        _cachedSubtitleTracks = subtitleTracks;
        _lastTrackListCount = count;
        Log($"track-list rebuilt count={count} audio={audioTracks.Count} sub={subtitleTracks.Count} force={force}");
    }

    private string? GetTrackString(int index, string name)
    {
        return MpvNative.TryGetString(_handle, MpvProperty.TrackListProperty(index, name), out var value)
            ? value
            : null;
    }

    private bool GetTrackFlag(int index, string name)
    {
        return MpvNative.TryGetFlag(_handle, MpvProperty.TrackListProperty(index, name), out var value) && value;
    }

    // forceTrackReload 只在轨道集合可能变化时才需要（sub-add）。切换选中轨道、
    // 静音之类的操作不改变集合，走 count 比对即可，不必重读整个 track-list。
    private void PublishStateSnapshot(bool forceTrackReload = false)
    {
        PlayerState snapshot;
        lock (_sync)
        {
            if (!_initialized || _handle == IntPtr.Zero)
            {
                return;
            }

            snapshot = PollStateLocked(forceTrackReload);
            _state = snapshot.Clone();
        }

        StateChanged?.Invoke(snapshot);
    }

    private void SetOptionLocked(string name, string value)
    {
        var result = MpvNative.SetOptionString(_handle, name, value);
        Log($"option {name}={value} result={DescribeResult(result)}");
        MpvNative.ThrowIfError(result, $"set option {name}");
    }

    private void TrySetProperty(string name, string value, string logPrefix)
    {
        try
        {
            lock (_sync)
            {
                if (!_initialized || _handle == IntPtr.Zero)
                {
                    return;
                }

                var result = MpvNative.SetPropertyString(_handle, name, value);
                Log($"{logPrefix}={DescribeResult(result)}");
            }
        }
        catch (Exception ex)
        {
            Log($"{logPrefix}=failed {ex.Message}");
        }
    }

    private void TryCommand(string[] command, string logPrefix)
    {
        try
        {
            var state = State;
            if (!state.HasMedia || state.IsIdleActive)
            {
                return;
            }

            lock (_sync)
            {
                if (!_initialized || _handle == IntPtr.Zero)
                {
                    return;
                }

                var result = MpvNative.Command(_handle, command);
                Log($"{logPrefix}={DescribeResult(result)}");
            }
        }
        catch (Exception ex)
        {
            Log($"{logPrefix}=failed {ex.Message}");
        }
    }

    private void EnsureInitialized()
    {
        lock (_sync)
        {
            if (!_initialized || _handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("libmpv is not initialized.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MpvPlayer));
        }
    }

    private static string DescribeResult(int result)
    {
        return result < 0 ? $"{MpvNative.ErrorString(result)} ({result})" : result.ToString(CultureInfo.InvariantCulture);
    }
}
