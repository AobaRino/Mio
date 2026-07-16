using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

    private readonly object _sync = new();
    private IntPtr _handle;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private PlayerState _state = PlayerState.CreateIdle();
    private string? _currentFile;
    private IntPtr _lastSwapChain;
    private int _lastCompositionWidth;
    private int _lastCompositionHeight;
    private bool _loadFileSubmitted;
    private int _lastSwapChainResult;
    private long _lastSwapChainRaw;
    private bool _initialized;
    private bool _disposed;

    public event Action<PlayerState>? StateChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<IntPtr>? SwapChainChanged;

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

            _initialized = true;
        }

        StartPolling();
    }

    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Media file was not found.", fullPath);
        }

        PlayerState snapshot;
        lock (_sync)
        {
            Log($"loadfile path={fullPath}");
            _loadFileSubmitted = false;
            var result = MpvNative.Command(_handle, MpvCommand.LoadFile(fullPath));
            Log($"loadfile result={DescribeResult(result)}");
            MpvNative.ThrowIfError(result, "loadfile");

            _currentFile = fullPath;
            _lastSwapChain = IntPtr.Zero;
            _loadFileSubmitted = result >= 0;
            _state = _state.Clone();
            _state.CurrentFile = fullPath;
            _state.MediaTitle = Path.GetFileName(fullPath);
            _state.HasMedia = true;
            _state.IsIdleActive = false;
            _state.IsEndOfFile = false;
            _state.IsSwapChainReady = false;
            snapshot = _state.Clone();
        }

        StateChanged?.Invoke(snapshot);
        await WaitForDisplaySwapChainAsync(cancellationToken);
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
        PublishStateSnapshot();
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
        Log("dispose complete");
    }

    private void StartPolling()
    {
        _pollCancellation = new CancellationTokenSource();
        _pollTask = PollStateAsync(_pollCancellation.Token);
    }

    private async Task WaitForDisplaySwapChainAsync(CancellationToken cancellationToken)
    {
        Log("waiting display-swapchain");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        var nextDiagnostic = DateTimeOffset.MinValue;
        var diagnostics = new DisplaySwapChainDiagnostics();

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr swapChain = IntPtr.Zero;
            PlayerState snapshot;
            var changed = false;

            lock (_sync)
            {
                diagnostics = ReadDisplaySwapChainDiagnosticsLocked();
                _lastSwapChainResult = diagnostics.SwapChainResult;
                _lastSwapChainRaw = diagnostics.SwapChainRaw;

                if (diagnostics.SwapChainPointer != IntPtr.Zero)
                {
                    swapChain = diagnostics.SwapChainPointer;
                    if (swapChain != _lastSwapChain)
                    {
                        _lastSwapChain = swapChain;
                        _state = _state.Clone();
                        _state.IsSwapChainReady = true;
                        changed = true;
                    }
                }

                snapshot = _state.Clone();
            }

            if (swapChain != IntPtr.Zero)
            {
                Log($"display-swapchain result={diagnostics.SwapChainResult} raw={diagnostics.SwapChainRaw} ptr=0x{swapChain.ToInt64():X}");
                if (changed)
                {
                    StateChanged?.Invoke(snapshot);
                    SwapChainChanged?.Invoke(swapChain);
                }

                return;
            }

            if (DateTimeOffset.UtcNow >= nextDiagnostic)
            {
                Log(diagnostics.ToLogLine());
                nextDiagnostic = DateTimeOffset.UtcNow.AddMilliseconds(250);
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            diagnostics = ReadDisplaySwapChainDiagnosticsLocked();
        }

        var message = diagnostics.ToTimeoutMessage();
        ErrorOccurred?.Invoke(message);
        throw new MpvException(message);
    }

    private DisplaySwapChainDiagnostics ReadDisplaySwapChainDiagnosticsLocked()
    {
        var diagnostics = new DisplaySwapChainDiagnostics
        {
            CurrentFile = _currentFile,
            LoadFileOk = _loadFileSubmitted,
            CompositionWidth = _lastCompositionWidth,
            CompositionHeight = _lastCompositionHeight
        };

        if (!_initialized || _handle == IntPtr.Zero)
        {
            diagnostics.SwapChainResult = int.MinValue;
            return diagnostics;
        }

        diagnostics.SwapChainResult = MpvNative.TryGetInt64WithResult(_handle, MpvProperty.DisplaySwapChain, out var swapChainRaw);
        diagnostics.SwapChainRaw = swapChainRaw;
        diagnostics.SwapChainPointer = swapChainRaw == 0 ? IntPtr.Zero : new IntPtr(swapChainRaw);
        if (diagnostics.SwapChainResult < 0)
        {
            diagnostics.SwapChainError = MpvNative.ErrorString(diagnostics.SwapChainResult);
        }

        diagnostics.DurationResult = MpvNative.TryGetDoubleWithResult(_handle, MpvProperty.Duration, out var duration);
        diagnostics.Duration = duration;
        diagnostics.IdleActiveResult = MpvNative.TryGetFlagWithResult(_handle, MpvProperty.IdleActive, out var idleActive);
        diagnostics.IdleActive = idleActive;
        diagnostics.PauseResult = MpvNative.TryGetFlagWithResult(_handle, MpvProperty.Pause, out var pause);
        diagnostics.Pause = pause;
        diagnostics.TimePositionResult = MpvNative.TryGetDoubleWithResult(_handle, MpvProperty.TimePosition, out var timePosition);
        diagnostics.TimePosition = timePosition;
        return diagnostics;
    }

    private async Task PollStateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
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

    private PlayerState PollStateLocked()
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
            ?? Path.GetFileName(_currentFile)
            ?? "Mio";
        next.HasMedia = !next.IsIdleActive && !string.IsNullOrWhiteSpace(_currentFile);
        next.IsSwapChainReady = _lastSwapChain != IntPtr.Zero;
        if (next.HasMedia)
        {
            var (audioTracks, subtitleTracks) = ReadTrackListsLocked();
            next.AudioTracks = audioTracks;
            next.SubtitleTracks = subtitleTracks;
            next.SelectedAudioTrackId = FindSelectedTrackId(audioTracks);
            next.SelectedSubtitleTrackId = FindSelectedTrackId(subtitleTracks);
            if (MpvNative.TryGetFlag(_handle, MpvProperty.SubVisibility, out var subtitlesVisible))
            {
                next.SubtitlesVisible = subtitlesVisible;
            }
        }
        else
        {
            next.AudioTracks = Array.Empty<TrackInfo>();
            next.SubtitleTracks = Array.Empty<TrackInfo>();
            next.SelectedAudioTrackId = null;
            next.SelectedSubtitleTrackId = null;
            next.SubtitlesVisible = false;
        }

        return next;
    }

    private (IReadOnlyList<TrackInfo> AudioTracks, IReadOnlyList<TrackInfo> SubtitleTracks) ReadTrackListsLocked()
    {
        var audioTracks = new List<TrackInfo>();
        var subtitleTracks = new List<TrackInfo>();

        if (!MpvNative.TryGetInt64(_handle, MpvProperty.TrackListCount, out var rawCount) || rawCount <= 0)
        {
            return (audioTracks, subtitleTracks);
        }

        var count = (int)Math.Min(rawCount, 512);
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
            var isSelected = GetTrackFlag(index, "selected");
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
                IsSelected = isSelected,
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

        return (audioTracks, subtitleTracks);
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

    private static int? FindSelectedTrackId(IEnumerable<TrackInfo> tracks)
    {
        return tracks.FirstOrDefault(track => track.IsSelected)?.Id;
    }

    private void PublishStateSnapshot()
    {
        PlayerState snapshot;
        lock (_sync)
        {
            if (!_initialized || _handle == IntPtr.Zero)
            {
                return;
            }

            snapshot = PollStateLocked();
            _state = snapshot.Clone();
        }

        StateChanged?.Invoke(snapshot);
    }

    private void SetOptionLocked(string name, string value)
    {
        var result = MpvNative.SetOptionString(_handle, name, value);
        Log($"option {name}={value} {DescribeResultWithError(result)}");
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

    private static string DescribeResultWithError(int result)
    {
        return result < 0
            ? $"result={result} error={MpvNative.ErrorString(result)}"
            : $"result={result} error=success";
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[Mio.WinUI] {message}");
    }

    private sealed class DisplaySwapChainDiagnostics
    {
        public int SwapChainResult { get; set; }

        public long SwapChainRaw { get; set; }

        public IntPtr SwapChainPointer { get; set; }

        public string? SwapChainError { get; set; }

        public int DurationResult { get; set; }

        public double Duration { get; set; }

        public int IdleActiveResult { get; set; }

        public bool IdleActive { get; set; }

        public int PauseResult { get; set; }

        public bool Pause { get; set; }

        public int TimePositionResult { get; set; }

        public double TimePosition { get; set; }

        public int CompositionWidth { get; set; }

        public int CompositionHeight { get; set; }

        public string? CurrentFile { get; set; }

        public bool LoadFileOk { get; set; }

        public string ToLogLine()
        {
            var error = SwapChainResult < 0 ? $" error={SwapChainError}" : string.Empty;
            return $"display-swapchain diag result={SwapChainResult}{error} raw={SwapChainRaw} ptr=0x{SwapChainPointer.ToInt64():X} duration={Duration:0.###} durationResult={DurationResult} idleActive={IdleActive} idleResult={IdleActiveResult} pause={Pause} pauseResult={PauseResult} timePos={TimePosition:0.###} timeResult={TimePositionResult} compositionSize={CompositionWidth}x{CompositionHeight} loadfileOk={LoadFileOk} file={CurrentFile ?? "<none>"}";
        }

        public string ToTimeoutMessage()
        {
            return string.Create(CultureInfo.InvariantCulture, $"""
display-swapchain not available.
loadfileOk={LoadFileOk}
duration={Duration:0.###}
idleActive={IdleActive}
timePos={TimePosition:0.###}
lastSwapChainRaw={SwapChainRaw}
lastSwapChainResult={SwapChainResult}
compositionSize={CompositionWidth}x{CompositionHeight}
currentFile={CurrentFile ?? "<none>"}
Check d3d11-output-mode=composition, d3d11-composition-size, and mpv D3D11 options.
""");
        }
    }
}
