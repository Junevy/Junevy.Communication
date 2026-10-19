using Junevy.Communication.DebugAssistant.Services;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>每 100 ms 把待处理的条目派发到界面线程刷新一次（计划 DA-D10）。</summary>
internal sealed class SessionLogFlusher : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public SessionLogFlusher(SessionLog log, IUiDispatcher dispatcher)
    {
        _loop = RunAsync(log, dispatcher, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static async Task RunAsync(SessionLog log, IUiDispatcher dispatcher, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (log.HasPending)
                {
                    dispatcher.Post(log.Flush);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
