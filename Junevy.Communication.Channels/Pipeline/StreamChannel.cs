using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 把 <see cref="Stream"/> 变成字节通道（设计文档 6.2、6.4 节，计划 8.1–8.2，D7）。
/// 填充循环从流读入内部 <see cref="Pipe"/>；解析循环从该管道读出、分帧，并经 <see cref="FrameRouter"/> 路由。
/// 发送经发送锁整帧写出；请求在 Sequential 模式下经请求锁串行化，并与路由表关联应答。
/// 连接丢失时经 <c>onFault</c> 报告，同一实例至多一次；主动停止（<see cref="StopAsync"/>）不报告。
/// </summary>
/// <remarks>
/// 对外方法不抛出 I/O 异常，失败以 <see cref="CommResult"/> 返回。<see cref="Start"/> 同时启动 <see cref="FrameRouter"/> 的派发循环，
/// 路由的生命周期由本类管理（停止时由本类停止路由）。
/// </remarks>
internal sealed class StreamChannel : IAsyncDisposable
{
    // Pipe 背压阈值（计划 8.2 第 5 条）：解析循环跟不上时填充循环挂起，形成对流的背压。
    private const int PauseWriterThreshold = 1024 * 1024;
    private const int ResumeWriterThreshold = 512 * 1024;

    private const string EmptyRequestMessage =
        "A request payload must not be empty; use ReceiveAsync to wait for a frame without sending one.";

    private readonly Stream stream;
    private readonly StreamChannelSettings settings;
    private readonly IFrameDecoder decoder;
    private readonly IFlushableFrameDecoder? flushable;
    private readonly IFrameEncoder encoder;
    private readonly PendingRequestTable table;
    private readonly FrameRouter router;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private readonly Action<DisconnectReason, Exception?> onFault;
    private readonly Action abortTransport;
    private readonly Pipe pipe;
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim requestLock = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource stopSource = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> stopCompletion =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private int started;
    private int stopRequested;
    private int faultReported;
    private Task? fillLoop;
    private Task? parseLoop;

    /// <summary>
    /// 创建流通道（未启动）。构造时复制并校验 <paramref name="settings"/>（D5），之后修改调用方的对象不影响本实例。
    /// </summary>
    /// <param name="stream">底层流；由 <paramref name="abortTransport"/> 负责在中止时销毁或打断它。</param>
    /// <param name="settings">运行参数。</param>
    /// <param name="decoder">分帧器（每条连接一个实例）。</param>
    /// <param name="encoder">编码器。</param>
    /// <param name="table">关联表。</param>
    /// <param name="router">帧路由；本类在 <see cref="Start"/> 时启动它，在停止时停止它。</param>
    /// <param name="statistics">连接统计。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="onFault">连接丢失时的回调（原因，异常）；同一实例至多调用一次。</param>
    /// <param name="abortTransport">中止底层传输（超时、用户取消写出、停止时调用）；必须可以重复调用。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">运行参数非法。</exception>
    public StreamChannel(Stream stream, StreamChannelSettings settings, IFrameDecoder decoder, IFrameEncoder encoder,
                         PendingRequestTable table, FrameRouter router, ConnectionStatistics statistics, ILogger logger,
                         Action<DisconnectReason, Exception?> onFault, Action abortTransport)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (decoder == null)
            throw new ArgumentNullException(nameof(decoder));
        if (encoder == null)
            throw new ArgumentNullException(nameof(encoder));
        if (table == null)
            throw new ArgumentNullException(nameof(table));
        if (router == null)
            throw new ArgumentNullException(nameof(router));
        if (statistics == null)
            throw new ArgumentNullException(nameof(statistics));
        if (logger == null)
            throw new ArgumentNullException(nameof(logger));
        if (onFault == null)
            throw new ArgumentNullException(nameof(onFault));
        if (abortTransport == null)
            throw new ArgumentNullException(nameof(abortTransport));

        this.settings = CopyAndValidate(settings);
        this.stream = stream;
        this.decoder = decoder;
        this.encoder = encoder;
        this.table = table;
        this.router = router;
        this.statistics = statistics;
        this.logger = logger;
        this.onFault = onFault;
        this.abortTransport = abortTransport;
        flushable = decoder as IFlushableFrameDecoder;

        pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            useSynchronizationContext: false));
    }

    /// <summary>启动填充循环、解析循环与帧路由的派发循环。只能调用一次。</summary>
    /// <exception cref="InvalidOperationException">已经启动。</exception>
    /// <exception cref="ObjectDisposedException">已经停止。</exception>
    public void Start()
    {
        if (IsStopping)
            throw new ObjectDisposedException(nameof(StreamChannel));
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The stream channel has already been started.");

        router.Start();
        fillLoop = Task.Run(FillLoopAsync);
        parseLoop = Task.Run(ParseLoopAsync);
    }

    /// <summary>
    /// 发送一帧（计划 8.2 发送流程）。经发送锁整帧写出，受 <see cref="StreamChannelSettings.SendTimeout"/> 约束。
    /// 等待发送锁时取消只返回 <c>Cancelled</c>；写出期间超时、取消或 I/O 错误都会中止传输并报告 <c>SendFailed</c>（D10）。
    /// </summary>
    /// <param name="payload">负载；编码器可能追加分隔符。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>发送结果。</returns>
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        => WriteFrameAsync(payload, cancellationToken);

    /// <summary>
    /// 发送一帧并等待应答（计划 8.2 请求流程）。负载为空时立即以 <c>InvalidRequest</c> 失败。
    /// </summary>
    /// <param name="payload">请求负载。</param>
    /// <param name="options">超时与匹配器；为 null 时使用配置的 RequestTimeout 与默认匹配。</param>
    /// <param name="expectedRemote">UDP 非定向模式的期望来源；其余传输为 null。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>应答帧或失败结果。</returns>
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options,
                                                       EndPoint? expectedRemote, CancellationToken cancellationToken)
    {
        if (payload.IsEmpty)
            return CommResult<byte[]>.Fail(EmptyRequestMessage, CommErrorKind.InvalidRequest);
        if (IsClosed)
            return ClosedFailureTyped();

        int timeout = options?.Timeout ?? settings.RequestTimeout;
        bool sequential = settings.Correlation == CorrelationMode.Sequential;
        bool holdsRequestLock = false;
        PendingRequest? waiter = null;
        try
        {
            if (sequential)
            {
                if (!await WaitForLockAsync(requestLock, cancellationToken).ConfigureAwait(false))
                    return CommResult<byte[]>.Fail("The request was cancelled while waiting for the request lock.", CommErrorKind.Cancelled);

                holdsRequestLock = true;
            }

            if (IsClosed)
                return ClosedFailureTyped();

            waiter = table.Register(payload, options?.Matcher, timeout, expectedRemote, cancellationToken);
            if (waiter.Completion.IsCompleted)
                return await waiter.Completion.ConfigureAwait(false);

            // 登记之后再检查：登记与报告故障（FailAll）并发时，等待者可能错过 FailAll，此时由这里返回。
            if (IsClosed)
                return ClosedFailureTyped();

            CommResult sent = await WriteFrameAsync(payload, cancellationToken).ConfigureAwait(false);
            if (!sent.IsSuccess)
                return sent.As<byte[]>();

            waiter.StartTimer();
            CommResult<byte[]> reply = await waiter.Completion.ConfigureAwait(false);
            if (sequential && (reply.ErrorKind == CommErrorKind.Timeout || reply.ErrorKind == CommErrorKind.Cancelled))
                await HoldRequestLockAfterExpiryAsync(timeout).ConfigureAwait(false);

            return reply;
        }
        finally
        {
            waiter?.Dispose();
            if (holdsRequestLock)
                requestLock.Release();
        }
    }

    /// <summary>
    /// 不发送，只等待下一个匹配的入站帧（计划 8.2 接收流程）。接收等待者在注册时开始计时。
    /// </summary>
    /// <param name="options">超时与匹配器；为 null 时使用默认值。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>匹配的帧或失败结果。</returns>
    public async Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options, CancellationToken cancellationToken)
    {
        if (IsClosed)
            return ClosedFailureTyped();

        int timeout = options?.Timeout ?? settings.RequestTimeout;
        PendingRequest waiter = table.Register(ReadOnlyMemory<byte>.Empty, options?.Matcher, timeout, null, cancellationToken);
        try
        {
            // 与 RequestAsync 相同：登记之后再检查，避免错过 FailAll。
            if (IsClosed)
                return ClosedFailureTyped();

            return await waiter.Completion.ConfigureAwait(false);
        }
        finally
        {
            waiter.Dispose();
        }
    }

    /// <summary>
    /// 停止通道（计划 8.2 停止流程）。顺序固定，防止与派发上下文互相等待（审阅者确定）：
    /// 设置停止标志 → 中止传输 → 完成内部管道并唤醒解析循环 → 启动路由停止 → 在 <paramref name="drainTimeout"/> 内等待两个循环退出
    /// → 以 <c>ConnectionClosed</c> 失败所有在途等待者 → 等待路由停止完成。
    /// 第二次及之后的调用等待同一次停止完成；停止不调用 <c>onFault</c>。
    /// </summary>
    /// <param name="drainTimeout">路由排空已收到帧的最长毫秒数（转交给 <see cref="FrameRouter.StopAsync"/>）；≤ 0 表示不排空。</param>
    /// <returns>停止完成的任务。</returns>
    public Task StopAsync(int drainTimeout)
    {
        if (Interlocked.Exchange(ref stopRequested, 1) == 0)
            _ = CompleteStopAsync(drainTimeout);

        return stopCompletion.Task;
    }

    /// <summary>等同于 <c>StopAsync(0)</c>。</summary>
    public ValueTask DisposeAsync() => new ValueTask(StopAsync(0));

    private bool IsStopping => Volatile.Read(ref stopRequested) != 0;

    private bool IsClosed => IsStopping || Volatile.Read(ref faultReported) != 0;

    private int IdleTimeoutMs => flushable != null ? flushable.FlushTimeout : settings.PartialFrameTimeout;

    private static CommResult ClosedFailure()
        => CommResult.Fail("The stream channel is closed.", CommErrorKind.ConnectionClosed);

    private static CommResult<byte[]> ClosedFailureTyped()
        => CommResult<byte[]>.Fail("The stream channel is closed.", CommErrorKind.ConnectionClosed);

    // 复制配置并校验（D5）：超时 ≤ 0 表示不限时，不在此拒绝。
    private static StreamChannelSettings CopyAndValidate(StreamChannelSettings source)
    {
        var copy = new StreamChannelSettings
        {
            SendTimeout = source.SendTimeout,
            RequestTimeout = source.RequestTimeout,
            LateReplyWindow = source.LateReplyWindow,
            PartialFrameAction = source.PartialFrameAction,
            PartialFrameTimeout = source.PartialFrameTimeout,
            ResetOnRequestTimeout = source.ResetOnRequestTimeout,
            Correlation = source.Correlation,
            ReceiveBufferSize = source.ReceiveBufferSize,
        };

        if (!Enum.IsDefined(typeof(CorrelationMode), copy.Correlation))
            throw new ArgumentOutOfRangeException(nameof(source), copy.Correlation, "Unknown correlation mode.");
        if (copy.LateReplyWindow < -1)
            throw new ArgumentOutOfRangeException(nameof(source), copy.LateReplyWindow, "The late reply window must be -1 or a non-negative value.");
        if (copy.PartialFrameTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(source), copy.PartialFrameTimeout, "The partial frame timeout must not be negative.");
        if (!Enum.IsDefined(typeof(PartialFrameAction), copy.PartialFrameAction))
            throw new ArgumentOutOfRangeException(nameof(source), copy.PartialFrameAction, "Unknown partial frame action.");
        if (copy.ReceiveBufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(source), copy.ReceiveBufferSize, "The receive buffer size must be positive.");

        return copy;
    }

    // 计划 8.2 请求第 4 条：Sequential 超时或取消等待后，重建连接，或在迟到窗口期内继续持有请求锁。
    private async Task HoldRequestLockAfterExpiryAsync(int timeout)
    {
        if (settings.ResetOnRequestTimeout)
        {
            Fault(DisconnectReason.RequestTimeout, null);
            return;
        }

        int window = ResolveLateReplyWindow(timeout);
        if (window <= 0)
            return;

        try
        {
            // 这段等待不受用户令牌约束；通道停止时立即结束。
            await Task.Delay(window, stopSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 通道正在停止：窗口提前结束。
        }
    }

    // 与 PendingRequestTable 的窗口规则一致：-1 表示等于本次请求超时，超时无限时窗口为 0。
    private int ResolveLateReplyWindow(int timeout)
    {
        if (settings.LateReplyWindow >= 0)
            return settings.LateReplyWindow;

        return timeout > 0 ? timeout : 0;
    }

    // 写出一帧（计划 8.2 发送流程）。发送锁串行化所有写出；等待锁期间取消不影响连接。
    private async Task<CommResult> WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken userToken)
    {
        if (IsClosed)
            return ClosedFailure();

        if (!await WaitForLockAsync(sendLock, userToken).ConfigureAwait(false))
            return CommResult.Fail("The send was cancelled while waiting for the send lock.", CommErrorKind.Cancelled);

        try
        {
            if (IsClosed)
                return ClosedFailure();

            // 锁获取之后、写出之前取消：尚未写出任何字节，不中止传输。
            if (userToken.IsCancellationRequested)
                return CommResult.Fail("The send was cancelled before any byte was written.", CommErrorKind.Cancelled);

            using PooledBufferWriter wire = EncodeFrame(payload);
            ReadOnlyMemory<byte> bytes = wire.WrittenMemory;
            if (bytes.IsEmpty)
                return CommResult.Success();

            return await WriteBytesAsync(bytes, userToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private PooledBufferWriter EncodeFrame(ReadOnlyMemory<byte> payload)
    {
        var wire = new PooledBufferWriter();
        try
        {
            encoder.Encode(payload.Span, wire);
        }
        catch
        {
            wire.Dispose();
            throw;
        }

        return wire;
    }

    // 整帧写出，受 SendTimeout 约束。超时、取消写出与 I/O 错误都中止传输并报告 SendFailed（D10）。
    private async Task<CommResult> WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken userToken)
    {
        using TimeoutScope scope = TimeoutScope.Start(settings.SendTimeout, userToken, OnWriteAborted);
        try
        {
            await WriteToStreamAsync(bytes, scope.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CommResult failure;
            if (scope.IsUserCancelled)
                failure = CommResult.Fail("The send was cancelled while the frame was being written.", CommErrorKind.Cancelled, null, ex);
            else if (scope.IsTimedOut)
                failure = CommResult.Fail($"The frame was not written within {settings.SendTimeout} ms.", CommErrorKind.Timeout, null, ex);
            else
                failure = CommResult.Fail("The connection was closed while writing a frame.", CommErrorKind.ConnectionClosed, null, ex);

            Fault(DisconnectReason.SendFailed, ex);
            return failure;
        }

        // 写出已完成：撤销计时，之后到期的计时器不再中止传输。
        scope.Dispose();
        statistics.RecordFrameSent(bytes.Length, DateTimeOffset.UtcNow);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("TX {Length} bytes: {Hex}", bytes.Length, HexFormatter.ToHex(bytes.Span));

        return CommResult.Success();
    }

    // 写出超时或被取消时由 TimeoutScope 调用：先报告 SendFailed，再中止传输。
    // 顺序很重要：中止会让读取端抛出，若先中止，读取端会抢先以 RemoteClosed 报告，掩盖真正的原因。
    private void OnWriteAborted()
    {
        Fault(DisconnectReason.SendFailed, null);
        SafeAbortTransport();
    }

    private async Task WriteToStreamAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
#if NET8_0_OR_GREATER
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
#else
        // net472：流的 Memory 重载不存在，使用数组重载（D7）。帧缓冲由 PooledBufferWriter 提供，始终由数组支持。
        if (!MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment))
            throw new InvalidOperationException("The frame buffer is not backed by an array.");

        await stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, token).ConfigureAwait(false);
#endif
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    // 填充循环（计划 8.2 填充循环）：流 → 内部管道。读到 0 字节或出错即报告并结束；停止时静默结束。
    private async Task FillLoopAsync()
    {
        PipeWriter writer = pipe.Writer;
        try
        {
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(settings.ReceiveBufferSize);
#if NET8_0_OR_GREATER
                int read = await stream.ReadAsync(memory, stopSource.Token).ConfigureAwait(false);
#else
                if (!MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment))
                    throw new InvalidOperationException("The pipe buffer is not backed by an array.");

                int read = await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, stopSource.Token).ConfigureAwait(false);
#endif
                if (read == 0)
                {
                    Fault(DisconnectReason.RemoteClosed, null);
                    return;
                }

                writer.Advance(read);

                // 解析循环跟不上时 FlushAsync 挂起，形成背压（TCP 由此触发流控）。
                FlushResult flush = await writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCanceled || flush.IsCompleted)
                    return;
            }
        }
        catch (Exception ex)
        {
            if (!IsStopping)
                Fault(ex is IOException ? DisconnectReason.RemoteClosed : DisconnectReason.Error, ex);
        }
        finally
        {
            writer.Complete();
        }
    }

    // 解析循环（计划 8.2 解析循环）：内部管道 → 分帧 → 路由。半帧 / 静默计时见 IdleTimer。
    private async Task ParseLoopAsync()
    {
        PipeReader reader = pipe.Reader;
        var idle = new IdleTimer(reader, IdleTimeoutMs);
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
                if (IsStopping)
                    return;

                ReadOnlySequence<byte> buffer = result.Buffer;
                long seenLength = buffer.Length;
                bool flushDue = false;

                if (idle.IsArmed && seenLength == idle.ArmedLength)
                {
                    // 自计时开始没有新数据：要么计时器尚未到期（提前唤醒或残留取消，按剩余时间继续等待），要么已到期。
                    long now = Stopwatch.GetTimestamp();
                    if (!idle.IsDue(now))
                    {
                        idle.Resume(now);
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        if (result.IsCompleted)
                            return;

                        continue;
                    }

                    idle.Disarm();
                    if (flushable != null)
                    {
                        flushDue = true;
                    }
                    else if (settings.PartialFrameAction == PartialFrameAction.Disconnect)
                    {
                        statistics.IncrementProtocolErrors();
                        Fault(DisconnectReason.PartialFrameTimeout, null);
                        return;
                    }
                    else
                    {
                        statistics.IncrementProtocolErrors();
                        logger.LogWarning("Discarded {Length} byte(s) of an incomplete frame after {Timeout} ms without new data.",
                            buffer.Length, settings.PartialFrameTimeout);
                        buffer = buffer.Slice(buffer.End);
                    }
                }

                while (!buffer.IsEmpty)
                {
                    byte[] frame;
                    try
                    {
                        ReadOnlySequence<byte> sequence;
                        bool produced;
                        if (flushDue)
                            produced = flushable!.TryFlush(ref buffer, out sequence);
                        else
                            produced = decoder.TryDecode(ref buffer, out sequence);

                        if (!produced)
                            break;

                        frame = sequence.ToArray();
                    }
                    catch (FrameDecodeException ex)
                    {
                        if (HandleProtocolError(ex))
                            return;

                        buffer = buffer.Slice(buffer.End);
                        break;
                    }

                    statistics.RecordFrameReceived(frame.Length, DateTimeOffset.UtcNow);
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("RX {Length} bytes: {Hex}", frame.Length, HexFormatter.ToHex(frame));

                    try
                    {
                        await router.RouteAsync(frame, null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (FrameDecodeException ex)
                    {
                        // 握手积压溢出（D8）。
                        if (HandleProtocolError(ex))
                            return;

                        buffer = buffer.Slice(buffer.End);
                        break;
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                    return;

                if (buffer.IsEmpty)
                    idle.Disarm();
                else if (idle.IsEnabled)
                    idle.Arm(buffer.Length);
            }
        }
        catch (Exception ex)
        {
            // 停止期间路由被关闭（ObjectDisposedException / ChannelClosedException）是正常退出，不报告。
            if (!IsStopping)
            {
                logger.LogError(ex, "The parse loop of the stream channel failed.");
                Fault(DisconnectReason.Error, ex);
            }
        }
        finally
        {
            idle.Dispose();
        }
    }

    // 协议错误（计划 8.2 解析第 6 条）：Disconnect 报告 ProtocolViolation 并返回 true；Discard 返回 false，调用方丢弃缓冲。
    private bool HandleProtocolError(FrameDecodeException exception)
    {
        statistics.IncrementProtocolErrors();
        if (settings.PartialFrameAction == PartialFrameAction.Disconnect)
        {
            Fault(DisconnectReason.ProtocolViolation, exception);
            return true;
        }

        logger.LogWarning(exception, "A protocol violation was discarded; the buffered bytes are dropped.");
        return false;
    }

    // 连接丢失（计划 8.2）：同一实例至多报告一次。先以 ConnectionClosed 失败在途等待者，再调用 onFault。
    // 停止过程中的任何故障都不报告（停止由所有者发起）。
    private void Fault(DisconnectReason reason, Exception? exception)
    {
        if (IsStopping || Interlocked.Exchange(ref faultReported, 1) != 0)
            return;

        logger.LogWarning(exception, "The stream channel lost its connection: {Reason}.", reason);
        table.FailAll(CommResult.Fail("The connection was lost.", CommErrorKind.ConnectionClosed, null, exception));
        try
        {
            onFault(reason, exception);
        }
        catch (Exception callbackFailure)
        {
            logger.LogError(callbackFailure, "The fault callback of the stream channel threw an exception.");
        }
    }

    private async Task CompleteStopAsync(int drainTimeout)
    {
        try
        {
            await PerformStopAsync(drainTimeout).ConfigureAwait(false);
            stopCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            stopCompletion.TrySetException(ex);
        }
    }

    // 停止顺序见 StopAsync 的文档。路由停止必须在等待循环之前启动：解析循环可能挂起在满队列上，
    // 而停止可能从 FrameReceived 处理器内发起，此时只有路由停止（完成派发队列写端）才能让解析循环退出。
    private async Task PerformStopAsync(int drainTimeout)
    {
        SafeAbortTransport();
        stopSource.Cancel();
        pipe.Writer.CancelPendingFlush();
        pipe.Writer.Complete();
        pipe.Reader.CancelPendingRead();

        Task routerStop = router.StopAsync(drainTimeout).AsTask();
        await WaitForLoopsAsync(drainTimeout).ConfigureAwait(false);

        table.FailAll(CommResult.Fail("The stream channel was stopped before the operation completed.", CommErrorKind.ConnectionClosed));
        await routerStop.ConfigureAwait(false);
    }

    private async Task WaitForLoopsAsync(int drainTimeout)
    {
        var loops = new List<Task>(2);
        if (fillLoop != null)
            loops.Add(fillLoop);
        if (parseLoop != null)
            loops.Add(parseLoop);

        if (loops.Count == 0)
            return;

        Task all = Task.WhenAll(loops);
        Task finished = await Task.WhenAny(all, Task.Delay(Math.Max(drainTimeout, 0))).ConfigureAwait(false);
        if (!ReferenceEquals(finished, all) && drainTimeout > 0)
            logger.LogWarning("The stream loops did not exit within {Timeout} ms after the stop request.", drainTimeout);
    }

    private void SafeAbortTransport()
    {
        try
        {
            abortTransport();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Aborting the transport failed; the stop continues.");
        }
    }

    // 等待发送锁或请求锁；取消返回 false（不影响连接）。
    private static async Task<bool> WaitForLockAsync(SemaphoreSlim gate, CancellationToken token)
    {
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析循环使用的半帧 / 静默计时。计时器只负责唤醒解析循环（<see cref="PipeReader.CancelPendingRead"/>）；
    /// 是否到期由截止时间戳判定，因此计时器的提前或残留唤醒不会误判。
    /// 仅由解析循环访问。
    /// </summary>
    private sealed class IdleTimer : IDisposable
    {
        private readonly System.Threading.Timer timer;
        private readonly int timeoutMs;
        private long deadline;
        private long armedLength = -1;

        public IdleTimer(PipeReader reader, int timeoutMs)
        {
            this.timeoutMs = timeoutMs;
            timer = new System.Threading.Timer(static state => ((PipeReader)state!).CancelPendingRead(), reader,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        /// <summary>是否启用（超时 > 0）。</summary>
        public bool IsEnabled => timeoutMs > 0;

        /// <summary>是否已武装（缓冲中有未成帧数据）。</summary>
        public bool IsArmed => armedLength >= 0;

        /// <summary>武装时缓冲中的字节数；没有新数据时缓冲长度与之相等。</summary>
        public long ArmedLength => armedLength;

        /// <summary>截止时间是否已到。</summary>
        public bool IsDue(long now) => now >= deadline;

        /// <summary>以当前时刻重新武装：截止时间 = 现在 + 超时（有新数据时调用）。</summary>
        public void Arm(long length)
        {
            deadline = Stopwatch.GetTimestamp() + ToTicks(timeoutMs);
            armedLength = length;
            timer.Change(timeoutMs, System.Threading.Timeout.Infinite);
        }

        /// <summary>按剩余时间重新安排唤醒，不改变截止时间（计时器提前唤醒时调用）。</summary>
        public void Resume(long now)
            => timer.Change(ToMilliseconds(deadline - now), System.Threading.Timeout.Infinite);

        /// <summary>撤销计时。</summary>
        public void Disarm()
        {
            armedLength = -1;
            timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        public void Dispose() => timer.Dispose();

        private static long ToTicks(int milliseconds)
            => (long)(milliseconds * (Stopwatch.Frequency / 1000.0));

        private static int ToMilliseconds(long ticks)
        {
            int milliseconds = (int)Math.Ceiling(ticks * 1000.0 / Stopwatch.Frequency);
            return Math.Max(1, milliseconds);
        }
    }
}
