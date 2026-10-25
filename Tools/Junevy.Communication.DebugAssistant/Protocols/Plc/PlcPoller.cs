using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>
/// 监控表的周期轮询（计划第 7.1 节、DA-D10）：每隔一段时间执行一轮；上一轮还没有完成时跳过本轮；
/// <see cref="Stop"/> 之后不再执行新的一轮。<see cref="RefreshAsync"/> 立即执行一轮（写入后用它刷新），
/// 并等待正在进行的一轮结束，两者不会同时执行。
/// </summary>
internal sealed class PlcPoller : IAsyncDisposable
{
    private readonly Func<IPlcClient, CancellationToken, Task> _round;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <param name="round">执行一轮轮询的动作（读取全部监控行）。动作不应抛出异常：单行的失败由它自己记录。</param>
    public PlcPoller(Func<IPlcClient, CancellationToken, Task> round) => _round = round;

    /// <summary>是否正在轮询。</summary>
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>开始轮询。已经在运行时不做任何事。</summary>
    public void Start(IPlcClient client, int intervalMs)
    {
        if (IsRunning)
        {
            return;
        }

        var stop = new CancellationTokenSource();
        _stop = stop;
        _loop = LoopAsync(client, intervalMs, stop.Token);
    }

    /// <summary>停止轮询：取消之后不再开始新的一轮（正在进行的一轮会被取消）。</summary>
    public void Stop()
    {
        if (_stop is not { } stop)
        {
            return;
        }

        _stop = null;
        stop.Cancel();
    }

    /// <summary>立即执行一轮；如果正在进行一轮，等它结束之后再执行。轮询没有运行时，使用 <paramref name="client"/> 执行。</summary>
    public async Task RefreshAsync(IPlcClient client, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _round(client, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is { } loop)
        {
            await loop.ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task LoopAsync(IPlcClient client, int intervalMs, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!await _gate.WaitAsync(0, CancellationToken.None))
                {
                    continue;
                }

                try
                {
                    await _round(client, cancellationToken);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
