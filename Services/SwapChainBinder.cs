using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Mio.Interop;
using static Mio.Diagnostics.MioLog;

namespace Mio.Services;

public enum SwapChainBindStatus
{
    Bound,
    Failed,
    InterfaceUnavailable
}

public readonly record struct SwapChainBindOutcome(SwapChainBindStatus Status, int HResult)
{
    // 面板尚未真正就绪时 SetSwapChain 会返回 E_FAIL，重载当前文件让 mpv 重新
    // 产出 swapchain 通常能救回来。其余 HRESULT 重载也没用。
    public bool CanRecoverByReload =>
        Status == SwapChainBindStatus.Failed && HResult == unchecked((int)0x80004005);
}

/// <summary>
/// 把 mpv 的 D3D11 composition swapchain 绑定到 SwapChainPanel，并处理面板未就绪时的重试。
/// 只负责绑定本身：失败后要不要重载文件由调用方通过 <see cref="Completed"/> 决定。
/// </summary>
public sealed class SwapChainBinder
{
    private const int MaxAttempts = 20;
    private const int EFail = unchecked((int)0x80004005);
    private const int ENoInterface = unchecked((int)0x80004002);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly SwapChainPanel _panel;
    private readonly Action _syncCompositionSize;

    private CancellationTokenSource? _cancellation;
    private long _generation;
    private IntPtr _boundSwapChain;

    public SwapChainBinder(SwapChainPanel panel, Action syncCompositionSize)
    {
        _panel = panel;
        _syncCompositionSize = syncCompositionSize;
    }

    public event Action<SwapChainBindOutcome>? Completed;

    public IntPtr BoundSwapChain => _boundSwapChain;

    public void Bind(IntPtr swapChain)
    {
        if (swapChain == IntPtr.Zero || swapChain == _boundSwapChain)
        {
            return;
        }

        Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _ = BindAsync(swapChain, _generation, cancellation.Token);
    }

    public void Cancel()
    {
        _generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    public void Clear()
    {
        if (_boundSwapChain == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var hr = SwapChainPanelInterop.SetSwapChain(_panel, IntPtr.Zero);
            Log($"clear SwapChain result={ComHelpers.FormatHResult(hr)}");
        }
        catch (Exception ex)
        {
            Log($"SetSwapChain clear failed: {ex.Message}");
        }
        finally
        {
            _boundSwapChain = IntPtr.Zero;
        }
    }

    private async Task BindAsync(IntPtr swapChain, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var hr = 0;
            var clearedPreviousSwapChain = false;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrent(generation, cancellationToken))
                {
                    return;
                }

                if (!_panel.IsLoaded || _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0)
                {
                    Log($"SetSwapChain attempt {attempt} delayed: panel not ready");
                    await Task.Delay(RetryDelay, cancellationToken);
                    continue;
                }

                if (!clearedPreviousSwapChain && _boundSwapChain != IntPtr.Zero)
                {
                    var clearHr = SwapChainPanelInterop.SetSwapChain(_panel, IntPtr.Zero);
                    Log($"clear previous SwapChain result={ComHelpers.FormatHResult(clearHr)}");
                    _boundSwapChain = IntPtr.Zero;
                    clearedPreviousSwapChain = true;
                }

                _syncCompositionSize();
                hr = SwapChainPanelInterop.SetSwapChain(_panel, swapChain);
                Log($"SetSwapChain attempt={attempt} generation={generation} result={ComHelpers.FormatHResult(hr)}");

                if (!ComHelpers.Failed(hr))
                {
                    Log($"SetSwapChain success ptr=0x{swapChain.ToInt64():X} HRESULT {ComHelpers.FormatHResult(hr)}");
                    _boundSwapChain = swapChain;
                    Completed?.Invoke(new SwapChainBindOutcome(SwapChainBindStatus.Bound, hr));
                    return;
                }

                if (hr != EFail)
                {
                    break;
                }

                await Task.Delay(RetryDelay, cancellationToken);
            }

            if (!IsCurrent(generation, cancellationToken))
            {
                return;
            }

            var status = hr == ENoInterface ? SwapChainBindStatus.InterfaceUnavailable : SwapChainBindStatus.Failed;
            Completed?.Invoke(new SwapChainBindOutcome(status, hr));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log($"SetSwapChain canceled generation={generation}");
        }
    }

    private bool IsCurrent(long generation, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested && generation == _generation;
    }
}
