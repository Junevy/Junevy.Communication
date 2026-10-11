using System.Net;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 数据报通道（设计文档 6.2，计划 15.2）。每个数据报直接成为一帧，不经过分帧器。语义与 <see cref="StreamChannel"/> 对齐：
/// 发送经发送锁整帧写出并受 SendTimeout 约束；Sequential 请求经请求锁串行化；请求超时后按 RequestRetryCount 原样重发，
/// 全部尝试超时后才进入迟到应答窗口。UDP 无法换连接，因此固定不重建：超时只走迟到窗口，不触发故障。
/// 定向模式只派发来自远端的数据报（D13：不调用 Connect，来源过滤在这里完成，两种模式走同一条路径）。
/// </summary>
/// <remarks>
/// 一次请求的全部尝试共用同一个 <see cref="PendingRequest"/>：重发期间收到之前尝试的应答同样计为成功，迟到的应答也由同一张关联表识别。
/// 每次尝试单独计时，因此不使用 <see cref="PendingRequest.StartTimer"/>（它只能启动一次，且到期即完成等待者）。
/// 请求编排、停止序列与故障报告与 <see cref="StreamChannel"/> 共用（<see cref="RequestCoordinator"/>、<see cref="ChannelStopSequence"/>）。
/// </remarks>
internal sealed class DatagramChannel : IAsyncDisposable
{
    private const string EmptyRequestMessage =
        "A request payload must not be empty; use ReceiveAsync to wait for a datagram without sending one.";

    private const string MissingDestinationMessage = "A destination endpoint is required for this operation.";

    private readonly IDatagramTransport transport;
    private readonly DatagramChannelSettings settings;
    private readonly PendingRequestTable table;
    private readonly FrameRouter router;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private readonly ChannelStopSequence stop;
    private readonly RequestCoordinator requests;
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    private int started;

    /// <summary>
    /// 创建数据报通道（未启动）。构造时复制并校验 <paramref name="settings"/>（D5）。
    /// </summary>
    /// <param name="transport">数据报传输；由 <paramref name="onFault"/> 之后的 <see cref="IDatagramTransport.Abort"/> 中止。</param>
    /// <param name="settings">运行参数。</param>
    /// <param name="table">关联表（握手期间的积压由它保存）。</param>
    /// <param name="router">帧路由；本类在 <see cref="Start"/> 时启动它，在停止时停止它。</param>
    /// <param name="statistics">连接统计。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="onFault">连接丢失时的回调（原因，异常）；同一实例至多调用一次。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">运行参数非法。</exception>
    public DatagramChannel(IDatagramTransport transport, DatagramChannelSettings settings, PendingRequestTable table, FrameRouter router,
                           ConnectionStatistics statistics, ILogger logger, Action<DisconnectReason, Exception?> onFault)
    {
        if (transport == null)
            throw new ArgumentNullException(nameof(transport));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (table == null)
            throw new ArgumentNullException(nameof(table));
        if (router == null)
            throw new ArgumentNullException(nameof(router));
        if (statistics == null)
            throw new ArgumentNullException(nameof(statistics));

        stop = new ChannelStopSequence(table, router, logger, onFault, transport.Abort, "datagram", "receive loop", unblockReaders: null);
        this.settings = CopyAndValidate(settings);
        this.transport = transport;
        this.table = table;
        this.router = router;
        this.statistics = statistics;
        this.logger = logger;
        requests = new RequestCoordinator(stop, table, this.settings.Correlation == CorrelationMode.Sequential,
            this.settings.RequestTimeout, this.settings.LateReplyWindow, resetOnExpiry: null);
    }

    /// <summary>本地端点（已绑定时）。</summary>
    public EndPoint? LocalEndPoint => transport.LocalEndPoint;

    /// <summary>定向模式的远端端点；非定向模式为 null。</summary>
    public EndPoint? DirectedRemote => transport.DirectedRemote;

    /// <summary>启动接收循环与帧路由的派发循环。只能调用一次。</summary>
    /// <exception cref="InvalidOperationException">已经启动。</exception>
    /// <exception cref="ObjectDisposedException">已经停止。</exception>
    public void Start()
    {
        if (stop.IsStopping)
            throw new ObjectDisposedException(nameof(DatagramChannel));
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The datagram channel has already been started.");

        router.Start();
        stop.AddLoop(Task.Run(ReceiveLoopAsync));
    }

    /// <summary>
    /// 发送一个数据报。经发送锁整帧写出，受 <see cref="DatagramChannelSettings.SendTimeout"/> 约束。
    /// 等待发送锁时取消只返回 <c>Cancelled</c>；写出期间超时、取消或 I/O 错误都会中止传输并报告 <c>SendFailed</c>（D10）。
    /// </summary>
    /// <param name="payload">负载；可以为空（发送零长度数据报）。</param>
    /// <param name="destination">目标地址；为 null 时以 <c>InvalidRequest</c> 失败。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>发送结果。</returns>
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, EndPoint? destination, CancellationToken cancellationToken)
    {
        if (destination == null)
            return Task.FromResult(CommResult.Fail(MissingDestinationMessage, CommErrorKind.InvalidRequest));

        return SendFrameAsync(payload, destination, cancellationToken);
    }

    /// <summary>
    /// 发送一个数据报并等待应答（计划 15.2 请求流程）。负载为空或目标为 null 时立即以 <c>InvalidRequest</c> 失败。
    /// </summary>
    /// <param name="payload">请求负载。</param>
    /// <param name="options">超时与匹配器；为 null 时使用配置的 RequestTimeout 与默认匹配。</param>
    /// <param name="destination">目标地址（同时作为期望的应答来源）。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>应答或失败结果。</returns>
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options, EndPoint? destination,
                                                       CancellationToken cancellationToken)
    {
        // 心跳探测的通道操作开始登记（可能随后排在请求锁上）：见 HeartbeatProbeScope.ChannelBusy。
        HeartbeatProbeScope.NoteChannelOperation();
        if (payload.IsEmpty)
            return CommResult<byte[]>.Fail(EmptyRequestMessage, CommErrorKind.InvalidRequest);
        if (destination == null)
            return CommResult<byte[]>.Fail(MissingDestinationMessage, CommErrorKind.InvalidRequest);

        EndPoint target = destination;
        return await requests.RequestAsync(payload, options, target,
            (waiter, timeout, token) => ExchangeAsync(payload, target, waiter, timeout, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 不发送，只等待下一个匹配的入站数据报（计划 15.2 接收流程）。接收等待者在注册时开始计时。
    /// </summary>
    /// <param name="options">超时与匹配器；为 null 时使用默认值。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>匹配的数据报或失败结果。</returns>
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options, CancellationToken cancellationToken)
        => requests.ReceiveAsync(options, cancellationToken);

    /// <summary>
    /// 停止通道。停止序列由 <see cref="ChannelStopSequence"/> 执行（与 <see cref="StreamChannel.StopAsync"/> 共用）：
    /// 设置停止标志 → 中止传输 → 取消接收循环 → 启动路由停止 → 在 <paramref name="drainTimeout"/> 内等待接收循环退出
    /// → 以 <c>ConnectionClosed</c> 失败所有在途等待者 → 等待路由停止完成。
    /// 第二次及之后的调用等待同一次停止完成；停止不调用 <c>onFault</c>。
    /// </summary>
    /// <param name="drainTimeout">路由排空已收到帧的最长毫秒数；≤ 0 表示不排空。</param>
    /// <returns>停止完成的任务。</returns>
    public Task StopAsync(int drainTimeout) => stop.Stop(drainTimeout);

    /// <summary>等同于 <c>StopAsync(0)</c>。</summary>
    public ValueTask DisposeAsync() => new ValueTask(StopAsync(0));

    // 复制配置并校验（D5）：超时 ≤ 0 表示不限时，不在此拒绝；其余数值的范围与通道构造期的结构校验一致。
    private static DatagramChannelSettings CopyAndValidate(DatagramChannelSettings source)
    {
        var copy = new DatagramChannelSettings
        {
            SendTimeout = source.SendTimeout,
            RequestTimeout = source.RequestTimeout,
            RequestRetryCount = source.RequestRetryCount,
            LateReplyWindow = source.LateReplyWindow,
            Correlation = source.Correlation,
            MaxDatagramSize = source.MaxDatagramSize,
        };

        if (!Enum.IsDefined(typeof(CorrelationMode), copy.Correlation))
            throw new ArgumentOutOfRangeException(nameof(source), copy.Correlation, "Unknown correlation mode.");
        if (copy.RequestRetryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(source), copy.RequestRetryCount, "The request retry count must not be negative.");
        if (copy.LateReplyWindow < -1)
            throw new ArgumentOutOfRangeException(nameof(source), copy.LateReplyWindow, "The late reply window must be -1 or a non-negative value.");
        if (copy.MaxDatagramSize < 1 || copy.MaxDatagramSize > 65507)
            throw new ArgumentOutOfRangeException(nameof(source), copy.MaxDatagramSize, "The maximum datagram size must be within [1, 65507].");

        return copy;
    }

    // 一次请求的发送与等待（计划 15.2）：超时后原样重发，每次重发重新计时；所有尝试都超时后，以 Timeout 结束并登记迟到窗口。
    // 发送失败立即返回（AwaitedReply 为 false），不进入迟到窗口，与 StreamChannel 一致。
    private async Task<RequestCoordinator.ExchangeOutcome> ExchangeAsync(ReadOnlyMemory<byte> payload, EndPoint destination, PendingRequest waiter,
                                                                         int timeout, CancellationToken userToken)
    {
        int attempts = timeout > 0 ? settings.RequestRetryCount + 1 : 1;
        for (int attempt = 1; ; attempt++)
        {
            // 应答可能恰好在上一次超时之后到达：已完成则不再重发。
            if (attempt > 1 && waiter.Completion.IsCompleted)
                return RequestCoordinator.ExchangeOutcome.AfterWait(await waiter.Completion.ConfigureAwait(false));

            CommResult sent = await SendFrameAsync(payload, destination, userToken).ConfigureAwait(false);
            if (!sent.IsSuccess)
                return RequestCoordinator.ExchangeOutcome.SendFailure(sent.As<byte[]>());

            waiter.MarkSent();
            if (timeout <= 0)
                return RequestCoordinator.ExchangeOutcome.AfterWait(await waiter.Completion.ConfigureAwait(false));

            if (await CompletedWithinAsync(waiter.Completion, timeout).ConfigureAwait(false))
                return RequestCoordinator.ExchangeOutcome.AfterWait(await waiter.Completion.ConfigureAwait(false));

            if (attempt >= attempts)
                break;

            logger.LogDebug("No reply within {Timeout} ms; resending the datagram (attempt {Attempt} of {Attempts}).", timeout, attempt + 1, attempts);
        }

        // 全部尝试都超时：从关联表中移除等待者并登记迟到应答窗口。若此时应答恰好已认领，结果以应答为准。
        table.OnTimeout(waiter, $"No reply was received after {attempts} attempt(s) of {timeout} ms each.");
        return RequestCoordinator.ExchangeOutcome.AfterWait(await waiter.Completion.ConfigureAwait(false));
    }

    // 等待一次尝试：等待者在 timeout 毫秒内完成则返回 true。计时器在返回前释放。
    private static async Task<bool> CompletedWithinAsync(Task completion, int timeout)
    {
        if (completion.IsCompleted)
            return true;

        using var attemptSource = new CancellationTokenSource();
        Task delay = Task.Delay(timeout, attemptSource.Token);
        await Task.WhenAny(completion, delay).ConfigureAwait(false);
        attemptSource.Cancel();
        return completion.IsCompleted;
    }

    // 发送一个数据报（发送锁串行化所有写出；等待锁期间取消不影响连接）。
    private async Task<CommResult> SendFrameAsync(ReadOnlyMemory<byte> payload, EndPoint destination, CancellationToken userToken)
    {
        HeartbeatProbeScope.NoteChannelOperation();
        if (stop.IsClosed)
            return stop.ClosedResult();

        if (!await RequestCoordinator.WaitForLockAsync(sendLock, userToken).ConfigureAwait(false))
            return CommResult.Fail("The send was cancelled while waiting for the send lock.", CommErrorKind.Cancelled);

        try
        {
            if (stop.IsClosed)
                return stop.ClosedResult();

            // 锁获取之后、写出之前取消：尚未写出任何字节，不中止传输。
            if (userToken.IsCancellationRequested)
                return CommResult.Fail("The send was cancelled before any byte was written.", CommErrorKind.Cancelled);

            return await WriteDatagramAsync(payload, destination, userToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    // 整个数据报写出，受 SendTimeout 约束。超时、取消写出与 I/O 错误都中止传输并报告 SendFailed（D10）。
    private async Task<CommResult> WriteDatagramAsync(ReadOnlyMemory<byte> payload, EndPoint destination, CancellationToken userToken)
    {
        stop.BeginWrite();
        using TimeoutScope scope = TimeoutScope.Start(settings.SendTimeout, userToken, OnWriteAborted);
        try
        {
            HeartbeatProbeScope.NoteWriteStarted();
            await transport.SendToAsync(payload, destination, scope.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommResult failure;
            if (scope.IsUserCancelled)
                failure = CommResult.Fail("The send was cancelled while the datagram was being written.", CommErrorKind.Cancelled, null, ex);
            else if (scope.IsTimedOut)
                failure = CommResult.Fail($"The datagram was not sent within {settings.SendTimeout} ms.", CommErrorKind.Timeout, null, ex);
            else
                failure = CommResult.Fail("The connection was closed while sending a datagram.", CommErrorKind.ConnectionClosed, null, ex);

            // 不依赖 TimeoutScope 的回调：取消写出时，写出异常可能先于回调到达，而 scope 释放会注销尚未执行的回调。
            stop.ReportSendFailure(ex);
            return failure;
        }

        // 写出已完成：撤销计时，之后到期的计时器不再中止传输。
        scope.Dispose();
        statistics.RecordFrameSent(payload.Length, DateTimeOffset.UtcNow);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("TX {Length} bytes to {Destination}: {Hex}", payload.Length, destination, HexFormatter.ToHex(payload.Span));

        return CommResult.Success();
    }

    // 写出超时或被取消时由 TimeoutScope 调用。
    private void OnWriteAborted() => stop.ReportSendFailure(null);

    // 接收循环：逐个接收数据报并交给 HandleDatagramAsync。停止过程中的异常（套接字被中止、取消）是正常退出；其它异常报告 Error（计划 15.2）。
    private async Task ReceiveLoopAsync()
    {
        CancellationToken stopToken = stop.StopToken;
        try
        {
            while (!stop.IsStopping)
            {
                DatagramReceipt datagram = await transport.ReceiveAsync(stopToken).ConfigureAwait(false);
                if (stop.IsStopping)
                    return;

                await HandleDatagramAsync(datagram).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (!stop.IsStopping)
                stop.Fault(DisconnectReason.Error, ex);
        }
    }

    // 处理一个数据报（计划 15.2）：定向模式丢弃非远端来源（FramesDropped）；超长丢弃（ProtocolErrors）；其余交给路由。
    private async Task HandleDatagramAsync(DatagramReceipt datagram)
    {
        EndPoint? directed = transport.DirectedRemote;
        if (directed != null && !directed.Equals(datagram.Remote))
        {
            statistics.IncrementFramesDropped();
            logger.LogDebug("Dropped a datagram of {Length} bytes from {Remote}; the directed channel accepts datagrams only from {Expected}.",
                datagram.Data.Length, datagram.Remote, directed);
            return;
        }

        if (datagram.Data.Length > settings.MaxDatagramSize)
        {
            statistics.IncrementProtocolErrors();
            logger.LogWarning("Dropped a datagram of {Length} bytes from {Remote}; the maximum datagram size is {Maximum} bytes.",
                datagram.Data.Length, datagram.Remote, settings.MaxDatagramSize);
            return;
        }

        statistics.RecordFrameReceived(datagram.Data.Length, DateTimeOffset.UtcNow);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("RX {Length} bytes from {Remote}: {Hex}", datagram.Data.Length, datagram.Remote, HexFormatter.ToHex(datagram.Data));

        try
        {
            await router.RouteAsync(datagram.Data, datagram.Remote, CancellationToken.None).ConfigureAwait(false);
        }
        catch (FrameDecodeException ex)
        {
            // 握手积压已满（D8）：数据报没有分帧状态可以损坏，丢弃本数据报并计为协议错误，不断开连接。
            statistics.IncrementProtocolErrors();
            logger.LogWarning(ex, "Dropped a datagram received during the handshake.");
        }
    }
}
