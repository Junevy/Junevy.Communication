using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Sdk;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.OpcUa;

/// <summary>
/// 连接、断开与释放（P62-D2、D3、D11、D12、P6-C8）。生命周期操作走同一把异步锁串行；
/// SDK 的重连与订阅转移由 <c>ManagedSession</c> 负责，这里不写第二个重连循环。
/// SDK 类型全部关在 <see cref="OpcUaSdkConnection"/> 里面（P6-C5）。
/// </summary>
internal sealed class OpcUaLifecycle : IAsyncDisposable
{
    private const string Busy = "A connection attempt is already in progress.";

    private readonly object owner;
    private readonly ILogger logger;
    private readonly OpcUaSessionStateMachine stateMachine;
    private readonly OpcUaSdkConnection connection;
    private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource shuttingDown = new CancellationTokenSource();

    private int disposed;

    /// <summary>
    /// 构造生命周期。
    /// </summary>
    /// <param name="owner">事件的 sender 与异常信息里的宿主。</param>
    /// <param name="options">已校验的选项。</param>
    /// <param name="loggerFactory">日志工厂，交给 SDK 的遥测上下文。</param>
    /// <param name="logger">本包的日志器。</param>
    /// <param name="stateMachine">状态机。</param>
    public OpcUaLifecycle(
        object owner,
        OpcUaClientOptions options,
        ILoggerFactory? loggerFactory,
        ILogger logger,
        OpcUaSessionStateMachine stateMachine)
    {
        this.owner = owner;
        this.logger = logger;
        this.stateMachine = stateMachine;
        connection = new OpcUaSdkConnection(options, loggerFactory, logger, stateMachine);
    }

    /// <summary>SDK 连接（读写、浏览与订阅都经它）。</summary>
    public OpcUaSdkConnection Connection => connection;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// 连接。已是 <see cref="OpcUaSessionState.Connected"/> 时直接成功；
    /// <see cref="OpcUaSessionState.Connecting"/> 或 <see cref="OpcUaSessionState.Reconnecting"/> 时返回
    /// <see cref="CommErrorKind.InvalidRequest"/>。首次连接失败不启动后台重连（P6-C11）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果。</returns>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, owner);

        if (stateMachine.State == OpcUaSessionState.Connected)
        {
            return CommResult.Success();
        }

        // 并发的连接要立刻拒绝，而不是排在锁后面等上一次连完再自己连一遍（P62-D2）。
        if (stateMachine.State is OpcUaSessionState.Connecting or OpcUaSessionState.Reconnecting)
        {
            return CommResult.Fail(Busy, CommErrorKind.InvalidRequest);
        }

        try
        {
            await gate.WaitAsync(shuttingDown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 排在别人后面的连接遇上释放：直接给失败结果，不要抛异常。
            return OpcUaSdkErrorMap.ConnectionClosed("The OPC UA client was disposed.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, owner);

            // 等锁期间可能已经有人连上了。
            if (stateMachine.State == OpcUaSessionState.Connected)
            {
                return CommResult.Success();
            }

            stateMachine.MoveTo(OpcUaSessionState.Connecting, null);

            CommResult connected = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!connected.IsSuccess)
            {
                stateMachine.MoveTo(OpcUaSessionState.Disconnected, connected.ErrorMessage);
                return connected;
            }

            // 会话挂上之后 SDK 的状态事件就开始生效：它要是已经报了断线（Reconnecting），不能再被这里改回 Connected。
            stateMachine.TryMoveTo(OpcUaSessionState.Connecting, OpcUaSessionState.Connected, null);
            return CommResult.Success();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 断开：先摘下会话、回到 <see cref="OpcUaSessionState.Disconnected"/>，再关闭服务器上的会话。可以重复调用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>断开结果；关闭失败只影响结果，状态照样是 <see cref="OpcUaSessionState.Disconnected"/>。</returns>
    public async Task<CommResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return OpcUaSdkErrorMap.ConnectionClosed("The OPC UA client is disposed.");
        }

        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // 顺序不能换：先摘下会话，它晚到的状态事件才不会把刚改好的 Disconnected 又改回去。
            OpcUaSdkSession? detached = connection.Detach();
            stateMachine.MoveTo(OpcUaSessionState.Disconnected, null);

            using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shuttingDown.Token);
            return await connection.CloseAsync(detached, closing.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 释放：先叫停进行中的连接，再在锁内摘下并释放会话。不等服务器对关闭的回应——SDK 释放会话时自己会关掉
    /// 服务器上的会话（用例核对服务器的会话数），重连期间也不会卡在不可达的服务器上。
    /// **不释放异步锁与令牌源**：它们可能正被进行中的调用使用（P6.1 验收发现 R3）。可重复调用。
    /// </summary>
    /// <returns>异步释放。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // 先让排队中的连接失败退出，而不是等它连上再断；
        // 已经在连的那条也要立刻叫停，否则释放会干等到 ConnectTimeout（F23）。
        shuttingDown.Cancel();
        connection.AbortConnect();

        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            OpcUaSdkSession? detached = connection.Detach();
            stateMachine.MoveTo(OpcUaSessionState.Disconnected, "The client was disposed.");
            await connection.CloseAsync(detached, shuttingDown.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
