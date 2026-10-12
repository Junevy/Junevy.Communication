using System.Net.Sockets;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 监听器的机械部分（P25 Task 13，补审第 2 节）：经工厂绑定监听器、接受循环（每个接入的连接交给调用方），
/// 以及故障后按退避策略重新绑定。服务端的状态（Running / Faulted）与监听器字段在服务端锁下维护，这里只通过回调进入。
/// </summary>
internal sealed class TcpListenerLoop
{
    private readonly TcpServerOptions options;
    private readonly TcpListenerFactory factory;
    private readonly string name;
    private readonly ILogger logger;

    /// <summary>创建监听器循环。</summary>
    /// <param name="options">服务端的已校验选项。</param>
    /// <param name="factory">绑定监听器的工厂。</param>
    /// <param name="name">服务端名称（用于日志与失败消息）。</param>
    /// <param name="logger">日志记录器。</param>
    public TcpListenerLoop(TcpServerOptions options, TcpListenerFactory factory, string name, ILogger logger)
    {
        this.options = options;
        this.factory = factory;
        this.name = name;
        this.logger = logger;
    }

    /// <summary>经工厂绑定监听器（失败时返回工厂给出的失败结果）。</summary>
    /// <returns>监听器，或失败结果。</returns>
    public CommResult<ITcpListener> Bind() => factory(options, name);

    /// <summary>
    /// 接受循环：每个接入的连接交给 <paramref name="admit"/>（其异常记录后关闭该连接）；接受失败（非停止原因）时调用
    /// <paramref name="onFailed"/> 并结束。
    /// </summary>
    /// <param name="listener">要接受连接的监听器。</param>
    /// <param name="admit">接入检查（接收已接受的套接字）。</param>
    /// <param name="onFailed">接受失败时的回调（监听器与异常）。</param>
    /// <returns>循环结束的任务。</returns>
    public async Task AcceptAsync(ITcpListener listener, Action<Socket> admit, Action<ITcpListener, Exception> onFailed)
    {
        while (true)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onFailed(listener, ex);
                return;
            }

            try
            {
                admit(accepted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Admitting a connection to TCP server {Name} failed.", name);
                accepted.Dispose();
            }
        }
    }

    /// <summary>
    /// 按退避策略重新绑定同一地址：每次绑定失败记 Warning；退避耗尽记 Error 并保持故障；绑定成功后交给 <paramref name="tryStart"/>。
    /// <paramref name="tryStart"/> 返回 false 表示服务端已停止，新监听器随即释放。被取消时静默结束。
    /// </summary>
    /// <param name="policy">退避策略。</param>
    /// <param name="token">取消令牌（停止或释放时取消）。</param>
    /// <param name="tryStart">在服务端锁下启动新监听器的回调。</param>
    /// <returns>重新监听结束的任务。</returns>
    public async Task RestartAsync(IBackoffPolicy policy, CancellationToken token, Func<ITcpListener, bool> tryStart)
    {
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                int? delay = policy.GetDelay(attempt);
                if (delay == null)
                {
                    logger.LogError("TCP server {Name} gave up re-listening after {Attempts} attempt(s); it stays faulted.", name, attempt - 1);
                    return;
                }

                await Task.Delay(delay.Value, token).ConfigureAwait(false);

                CommResult<ITcpListener> bound = Bind();
                if (!bound.IsSuccess)
                {
                    logger.LogWarning("Re-listening on {Name} failed (attempt {Attempt}): {Result}", name, attempt, bound);
                    continue;
                }

                if (!tryStart(bound.Data))
                {
                    bound.Data.Dispose();
                    return;
                }

                logger.LogInformation("TCP server {Name} re-listening after a fault.", name);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 停止或释放取消了重新监听。
        }
    }
}
