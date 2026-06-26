using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        ("keep-open", "no")
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
            var result = MpvNative.Command(_handle, MpvCommand.LoadFile(fullPath));
            Log($"loadfile result={DescribeResult(result)}");
            MpvNative.ThrowIfError(result, "loadfile");

            _currentFile = fullPath;
            _lastSwapChain = IntPtr.Zero;
            _state = _state.Clone();
            _state.CurrentFile = fullPath;
            _state.MediaTitle = Path.GetFileName(fullPath);
            _state.HasMedia = true;
            _state.IsIdleActive = false;
            _state.IsSwapChainReady = false;
            snapshot = _state.Clone();
        }

        StateChanged?.Invoke(snapshot);
        await WaitForDisplaySwapChainAsync(cancellationToken);
    }

    public void TogglePause()
    {
        var paused = State.IsPaused;
        TrySetProperty(MpvProperty.Pause, paused ? "no" : "yes", "pause result");
    }

    public void SeekRelative(double seconds)
    {
        TryCommand(MpvCommand.Seek(seconds, "relative"), "seek result");
    }

    public void SeekAbsolute(double seconds)
    {
        var state = State;
        if (!state.HasMedia || state.Duration <= 0 || state.IsIdleActive)
        {
            return;
        }

        var target = Math.Min(state.Duration, Math.Max(0, seconds));
        TryCommand(MpvCommand.Seek(target, "absolute"), "seek result");
    }

    public void SetVolume(double volume)
    {
        var clamped = Math.Min(100, Math.Max(0, volume));
        TrySetProperty(MpvProperty.Volume, clamped.ToString("0.###", CultureInfo.InvariantCulture), "volume result");
    }

    public void SetCompositionSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_initialized || _handle == IntPtr.Zero)
            {
                return;
            }

            if (_lastCompositionWidth == width && _lastCompositionHeight == height)
            {
                return;
            }

            _lastCompositionWidth = width;
            _lastCompositionHeight = height;
            var value = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
            var result = MpvNative.SetPropertyString(_handle, MpvProperty.D3D11CompositionSize, value);
            Log($"d3d11-composition-size set {value} result={DescribeResult(result)}");
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

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr swapChain = IntPtr.Zero;
            PlayerState snapshot;
            var changed = false;

            lock (_sync)
            {
                if (_initialized && _handle != IntPtr.Zero && MpvNative.TryGetInt64(_handle, MpvProperty.DisplaySwapChain, out var value))
                {
                    swapChain = new IntPtr(value);
                    if (swapChain != IntPtr.Zero && swapChain != _lastSwapChain)
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
                Log($"display-swapchain ptr=0x{swapChain.ToInt64():X}");
                if (changed)
                {
                    StateChanged?.Invoke(snapshot);
                    SwapChainChanged?.Invoke(swapChain);
                }

                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        var message = "display-swapchain not available. Check d3d11-output-mode=composition and D3D11 mpv options.";
        ErrorOccurred?.Invoke(message);
        throw new MpvException(message);
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

        if (MpvNative.TryGetDouble(_handle, MpvProperty.Volume, out var volume))
        {
            next.Volume = Math.Min(100, Math.Max(0, volume));
        }

        if (MpvNative.TryGetFlag(_handle, MpvProperty.IdleActive, out var idleActive))
        {
            next.IsIdleActive = idleActive;
        }

        next.CurrentFile = _currentFile;
        next.MediaTitle = MpvNative.GetPropertyString(_handle, MpvProperty.MediaTitle)
            ?? Path.GetFileName(_currentFile)
            ?? "Mio";
        next.HasMedia = !next.IsIdleActive && !string.IsNullOrWhiteSpace(_currentFile);
        next.IsSwapChainReady = _lastSwapChain != IntPtr.Zero;

        return next;
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

    private static void Log(string message)
    {
        Debug.WriteLine($"[Mio.WinUI] {message}");
    }
}
