using Junevy.Communication.DebugAssistant.Services;

namespace Junevy.Communication.DebugAssistant.Sessions;

/// <summary>按固定间隔把一个动作派发到界面线程（例如每秒刷新统计，计划第 5.3 节）。</summary>
internal sealed class UiTicker : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public UiTicker(TimeSpan interval, Action action, IUiDispatcher dispatcher)
    {
        _loop = RunAsync(interval, action, dispatcher, _stop.Token);
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

    private static async Task RunAsync(TimeSpan interval, Action action, IUiDispatcher dispatcher, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                dispatcher.Post(action);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
