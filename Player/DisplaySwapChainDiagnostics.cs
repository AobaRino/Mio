using System;
using System.Globalization;

namespace Mio.Player;

/// <summary>
/// display-swapchain 迟迟不可用时的现场快照。这些字段只服务于诊断日志与超时错误，
/// 不参与播放逻辑——等待循环本身只需要 swapchain 指针一个属性。
/// </summary>
internal sealed class DisplaySwapChainDiagnostics
{
    public int SwapChainResult { get; private set; }

    public long SwapChainRaw { get; private set; }

    public IntPtr SwapChainPointer { get; private set; }

    public string? SwapChainError { get; private set; }

    public int DurationResult { get; private set; }

    public double Duration { get; private set; }

    public int IdleActiveResult { get; private set; }

    public bool IdleActive { get; private set; }

    public int PauseResult { get; private set; }

    public bool Pause { get; private set; }

    public int TimePositionResult { get; private set; }

    public double TimePosition { get; private set; }

    public int CompositionWidth { get; private set; }

    public int CompositionHeight { get; private set; }

    public string? CurrentFile { get; private set; }

    /// <summary>
    /// 调用方必须持有 mpv handle 的同步锁。handle 为 Zero 表示尚未初始化或已销毁。
    /// </summary>
    public static DisplaySwapChainDiagnostics Read(
        IntPtr handle,
        string? currentFile,
        int compositionWidth,
        int compositionHeight)
    {
        var diagnostics = new DisplaySwapChainDiagnostics
        {
            CurrentFile = currentFile,
            CompositionWidth = compositionWidth,
            CompositionHeight = compositionHeight
        };

        if (handle == IntPtr.Zero)
        {
            diagnostics.SwapChainResult = int.MinValue;
            return diagnostics;
        }

        diagnostics.SwapChainResult = MpvNative.TryGetInt64WithResult(handle, MpvProperty.DisplaySwapChain, out var swapChainRaw);
        diagnostics.SwapChainRaw = swapChainRaw;
        diagnostics.SwapChainPointer = swapChainRaw == 0 ? IntPtr.Zero : new IntPtr(swapChainRaw);
        if (diagnostics.SwapChainResult < 0)
        {
            diagnostics.SwapChainError = MpvNative.ErrorString(diagnostics.SwapChainResult);
        }

        diagnostics.DurationResult = MpvNative.TryGetDoubleWithResult(handle, MpvProperty.Duration, out var duration);
        diagnostics.Duration = duration;
        diagnostics.IdleActiveResult = MpvNative.TryGetFlagWithResult(handle, MpvProperty.IdleActive, out var idleActive);
        diagnostics.IdleActive = idleActive;
        diagnostics.PauseResult = MpvNative.TryGetFlagWithResult(handle, MpvProperty.Pause, out var pause);
        diagnostics.Pause = pause;
        diagnostics.TimePositionResult = MpvNative.TryGetDoubleWithResult(handle, MpvProperty.TimePosition, out var timePosition);
        diagnostics.TimePosition = timePosition;
        return diagnostics;
    }

    public string ToLogLine()
    {
        var error = SwapChainResult < 0 ? $" error={SwapChainError}" : string.Empty;
        return $"display-swapchain diag result={SwapChainResult}{error} raw={SwapChainRaw} ptr=0x{SwapChainPointer.ToInt64():X} duration={Duration:0.###} durationResult={DurationResult} idleActive={IdleActive} idleResult={IdleActiveResult} pause={Pause} pauseResult={PauseResult} timePos={TimePosition:0.###} timeResult={TimePositionResult} compositionSize={CompositionWidth}x{CompositionHeight} file={CurrentFile ?? "<none>"}";
    }

    public string ToTimeoutMessage()
    {
        return string.Create(CultureInfo.InvariantCulture, $"""
display-swapchain not available.
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
