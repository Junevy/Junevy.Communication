using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Buffers;
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
/// 路由的生命周期由本类管理（停止时由本类停止路由）。请求编排、停止序列与故障报告与 <see cref="DatagramChannel"/> 共用
/// （<see cref="RequestCoordinator"/>、<see cref="ChannelStopSequence"/>），本类只保留字节流的填充、解析与写出。
/// </remarks>
internal sealed class StreamChannel : IAsyncDisposable
{
    // Pipe 背压阈值（计划 8.2 第 5 条）：解析循环跟不上时填充循环挂起，形成对流的背压。
    private const int PauseWriterThreshold = 1024 * 1024;
    private const int ResumeWriterThreshold = 512 * 1024;
    private const int FrameBufferCapacity = 256;

    private const string EmptyRequestMessage =
        "A request payload must not be empty; use ReceiveAsync to wait for a frame without sending one.";

    private readonly Stream stream;
    private readonly StreamChannelSettings settings;
    private readonly IFrameDecoder decoder;
    private readonly IFlushableFrameDecoder? flushable;
    private readonly IFrameEncoder encoder;
    private readonly FrameRouter router;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private readonly ChannelStopSequence stop;
    private readonly RequestCoordinator requests;
    private readonly Pipe pipe;
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    private int started;
    private volatile InboundEnd? inboundEnd;

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

        stop = new ChannelStopSequence(table, router, logger, onFault, abortTransport, "stream", "stream loops", UnblockPipeReaders);
        this.settings = CopyAndValidate(settings);
        this.stream = stream;
        this.decoder = decoder;
        this.encoder = encoder;
        this.router = router;
        this.statistics = statistics;
        this.logger = logger;
        flushable = decoder as IFlushableFrameDecoder;
        requests = new RequestCoordinator(stop, table, this.settings.Correlation == CorrelationMode.Sequential,
            this.settings.RequestTimeout, this.settings.LateReplyWindow, ResetAfterExpiry);

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
        if (stop.IsStopping)
            throw new ObjectDisposedException(nameof(StreamChannel));
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("The stream channel has already been started.");

        router.Start();
        stop.AddLoop(Task.Run(FillLoopAsync));
        stop.AddLoop(Task.Run(ParseLoopAsync));
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
        // 心跳探测的通道操作开始登记（可能随后排在请求锁上）：见 HeartbeatProbeScope.ChannelBusy。
        HeartbeatProbeScope.NoteChannelOperation();
        if (payload.IsEmpty)
            return CommResult<byte[]>.Fail(EmptyRequestMessage, CommErrorKind.InvalidRequest);

        return await requests.RequestAsync(payload, options, expectedRemote,
            (waiter, _, token) => ExchangeAsync(payload, waiter, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 不发送，只等待下一个匹配的入站帧（计划 8.2 接收流程）。接收等待者在注册时开始计时。
    /// </summary>
    /// <param name="options">超时与匹配器；为 null 时使用默认值。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>匹配的帧或失败结果。</returns>
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options, CancellationToken cancellationToken)
        => requests.ReceiveAsync(options, cancellationToken);

    /// <summary>
    /// 停止通道（计划 8.2 停止流程）。停止序列由 <see cref="ChannelStopSequence"/> 执行（与数据报通道共用）：
    /// 设置停止标志 → 中止传输 → 取消并解除管道阻塞 → 启动路由停止 → 在 <paramref name="drainTimeout"/> 内等待两个循环退出
    /// → 以 <c>ConnectionClosed</c> 失败所有在途等待者 → 等待路由停止完成。
    /// 第二次及之后的调用等待同一次停止完成；停止不调用 <c>onFault</c>。
    /// </summary>
    /// <param name="drainTimeout">路由排空已收到帧的最长毫秒数（转交给 <see cref="FrameRouter.StopAsync"/>）；≤ 0 表示不排空。</param>
    /// <returns>停止完成的任务。</returns>
    public Task StopAsync(int drainTimeout) => stop.Stop(drainTimeout);

    /// <summary>等同于 <c>StopAsync(0)</c>。</summary>
    public ValueTask DisposeAsync() => new ValueTask(StopAsync(0));

    private int IdleTimeoutMs => flushable != null ? flushable.FlushTimeout : settings.PartialFrameTimeout;

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

    // 字节流的请求超时后重建连接（ResetOnRequestTimeout）。心跳探测期间发出的请求不重建连接：探测失败由监视器按 MaxFailures 计数（见 HeartbeatProbeScope）。
    // 返回 true 表示已重建连接，请求编排不再持锁等待迟到窗口。
    private bool ResetAfterExpiry()
    {
        if (!settings.ResetOnRequestTimeout || HeartbeatProbeScope.IsActive)
            return false;

        stop.Fault(DisconnectReason.RequestTimeout, null);
        return true;
    }

    // 请求的交换步骤（字节流）：写出一帧；写出失败立即返回（不进入迟到窗口）；写出成功后启动计时并等待应答。
    private async Task<RequestCoordinator.ExchangeOutcome> ExchangeAsync(ReadOnlyMemory<byte> payload, PendingRequest waiter, CancellationToken token)
    {
        CommResult sent = await WriteFrameAsync(payload, token).ConfigureAwait(false);
        if (!sent.IsSuccess)
            return RequestCoordinator.ExchangeOutcome.SendFailure(sent.As<byte[]>());

        waiter.StartTimer();
        return RequestCoordinator.ExchangeOutcome.AfterWait(await waiter.Completion.ConfigureAwait(false));
    }

    // 停止时解除管道读写者的阻塞：在取消之后、等待循环之前执行，使解析循环与填充循环能够退出。
    private void UnblockPipeReaders()
    {
        pipe.Writer.CancelPendingFlush();
        pipe.Writer.Complete();
        pipe.Reader.CancelPendingRead();
    }

    // 写出一帧（计划 8.2 发送流程）。发送锁串行化所有写出；等待锁期间取消不影响连接。
    private async Task<CommResult> WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken userToken)
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

            using ByteBufferWriter wire = EncodeFrame(payload);
            ReadOnlyMemory<byte> bytes = wire.WrittenMemory;
            if (bytes.IsEmpty)
                return CommResult.Success();

            HeartbeatProbeScope.NoteWriteStarted();
            return await WriteBytesAsync(bytes, userToken).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private ByteBufferWriter EncodeFrame(ReadOnlyMemory<byte> payload)
    {
        var wire = new ByteBufferWriter(FrameBufferCapacity, ArrayPool<byte>.Shared);
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
        stop.BeginWrite();
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

            // 不依赖 TimeoutScope 的回调：取消写出时，写出异常可能先于回调到达，而 scope 释放会注销尚未执行的回调。
            stop.ReportSendFailure(ex);
            return failure;
        }

        // 写出已完成：撤销计时，之后到期的计时器不再中止传输。
        scope.Dispose();
        statistics.RecordFrameSent(bytes.Length, DateTimeOffset.UtcNow);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("TX {Length} bytes: {Hex}", bytes.Length, HexFormatter.ToHex(bytes.Span));

        return CommResult.Success();
    }

    // 写出超时或被取消时由 TimeoutScope 调用。
    private void OnWriteAborted() => stop.ReportSendFailure(null);

    private async Task WriteToStreamAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
#if NET8_0_OR_GREATER
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
#else
        // net472：流的 Memory 重载不存在，使用数组重载（D7）。帧缓冲由 ByteBufferWriter 提供，始终由数组支持。
        if (!MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment))
            throw new InvalidOperationException("The frame buffer is not backed by an array.");

        await stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, token).ConfigureAwait(false);
#endif
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    // 填充循环（计划 8.2 填充循环）：流 → 内部管道。读到 0 字节或出错时只记录结束原因并完成管道写端；
    // 故障报告推迟到解析循环处理完已收到的数据之后（见 ParseLoopAsync）。
    private async Task FillLoopAsync()
    {
        PipeWriter writer = pipe.Writer;
        try
        {
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(settings.ReceiveBufferSize);
#if NET8_0_OR_GREATER
                int read = await stream.ReadAsync(memory, stop.StopToken).ConfigureAwait(false);
#else
                // net472：流的 Memory 重载不存在，使用数组重载（D7）。管道的缓冲由数组支持。
                if (!MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment))
                    throw new InvalidOperationException("The pipe buffer is not backed by an array.");

                int read = await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, stop.StopToken).ConfigureAwait(false);
#endif
                if (read == 0)
                {
                    RecordInboundEnd(DisconnectReason.RemoteClosed, null);
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
            RecordInboundEnd(ex is IOException ? DisconnectReason.RemoteClosed : DisconnectReason.Error, ex);
        }
        finally
        {
            // 完成写端：解析循环处理完缓冲中的数据后观察到结束。
            writer.Complete();
        }
    }

    // 记录入站端结束的原因（由填充循环调用）。停止过程中的结束由停止流程发起，不记录。
    private void RecordInboundEnd(DisconnectReason reason, Exception? exception)
    {
        if (!stop.IsStopping)
            inboundEnd = new InboundEnd(reason, exception);
    }

    // 解析循环（计划 8.2 解析循环）：内部管道 → 分帧 → 路由。半帧 / 静默计时见 IdleTimer。
    // 对端结束（result.IsCompleted）时先处理完缓冲中的全部数据，再报告记录的原因。
    private async Task ParseLoopAsync()
    {
        PipeReader reader = pipe.Reader;
        var idle = new IdleTimer(reader, IdleTimeoutMs);
        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
                if (stop.IsStopping)
                    return;

                ReadOnlySequence<byte> buffer = result.Buffer;
                bool flushDue = false;

                // 对端已结束时不再等待计时器：残留数据直接按结束处理。
                if (!result.IsCompleted && idle.IsArmed && buffer.Length == idle.ArmedLength)
                {
                    SilenceOutcome silence = HandleSilence(reader, idle, ref buffer);
                    if (silence == SilenceOutcome.NotDue)
                        continue;

                    if (silence == SilenceOutcome.Disconnected)
                        return;

                    flushDue = silence == SilenceOutcome.Flush;
                }

                DecodeResult decoded = await DecodeAndRouteAsync(buffer, flushDue).ConfigureAwait(false);
                if (decoded.Disconnected)
                    return;

                buffer = decoded.Remaining;
                if (result.IsCompleted)
                {
                    await FinishInboundAsync(reader, buffer).ConfigureAwait(false);
                    return;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (buffer.IsEmpty)
                    idle.Disarm();
                else if (idle.IsEnabled)
                    idle.Arm(buffer.Length);
            }
        }
        catch (Exception ex)
        {
            ReportParseFailure(ex);
        }
        finally
        {
            idle.Dispose();
        }
    }

    // 解析循环失败：停止期间路由被关闭（ObjectDisposedException / ChannelClosedException）是正常退出，不报告。
    private void ReportParseFailure(Exception ex)
    {
        if (stop.IsStopping)
            return;

        logger.LogError(ex, "The parse loop of the stream channel failed.");
        Fault(DisconnectReason.Error, ex);
    }

    // 自计时开始没有新数据：计时器尚未到期（提前唤醒或残留取消）时按剩余时间继续等待；已到期时，可刷新的分帧器整段交出，
    // 否则按 PartialFrameAction 断开或丢弃未成帧的数据。
    private SilenceOutcome HandleSilence(PipeReader reader, IdleTimer idle, ref ReadOnlySequence<byte> buffer)
    {
        long now = Stopwatch.GetTimestamp();
        if (!idle.IsDue(now))
        {
            idle.Resume(now);
            reader.AdvanceTo(buffer.Start, buffer.End);
            return SilenceOutcome.NotDue;
        }

        idle.Disarm();
        if (flushable != null)
            return SilenceOutcome.Flush;

        if (settings.PartialFrameAction == PartialFrameAction.Disconnect)
        {
            statistics.IncrementProtocolErrors();
            Fault(DisconnectReason.PartialFrameTimeout, null);
            return SilenceOutcome.Disconnected;
        }

        statistics.IncrementProtocolErrors();
        logger.LogWarning("Discarded {Length} byte(s) of an incomplete frame after {Timeout} ms without new data.",
            buffer.Length, settings.PartialFrameTimeout);
        buffer = buffer.Slice(buffer.End);
        return SilenceOutcome.Discard;
    }

    // 对端已结束：残留数据不能丢失。可刷新分帧器整段交出；其余分帧器的残留半帧丢弃并记录。
    // 协议错误导致断开时直接返回（故障已报告）；否则处理完毕后报告记录的原因。
    private async Task FinishInboundAsync(PipeReader reader, ReadOnlySequence<byte> buffer)
    {
        if (!buffer.IsEmpty && flushable != null)
        {
            DecodeResult decoded = await DecodeAndRouteAsync(buffer, flush: true).ConfigureAwait(false);
            if (decoded.Disconnected)
                return;

            buffer = decoded.Remaining;
        }

        if (!buffer.IsEmpty)
        {
            statistics.IncrementProtocolErrors();
            logger.LogWarning("The remote endpoint closed with {Length} byte(s) of an incomplete frame; the bytes were discarded.", buffer.Length);
        }

        reader.AdvanceTo(buffer.End);
        ReportInboundEnd();
    }

    // 对端结束时报告记录的原因。未记录原因表示结束由停止导致，不报告。
    private void ReportInboundEnd()
    {
        InboundEnd? end = inboundEnd;
        if (end != null)
            Fault(end.Reason, end.Exception);
    }

    // 解码并派发 buffer 中的全部完整帧；flush 为 true 时整段刷新为一帧（仅可刷新分帧器）。
    // 返回剩余缓冲；协议错误导致断开时 Disconnected 为 true（故障已报告）。
    private async Task<DecodeResult> DecodeAndRouteAsync(ReadOnlySequence<byte> buffer, bool flush)
    {
        while (!buffer.IsEmpty)
        {
            byte[] frame;
            try
            {
                if (!TryProduceFrame(ref buffer, flush, out frame))
                    break;
            }
            catch (FrameDecodeException ex)
            {
                return ProtocolFailure(ex, buffer);
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
                return ProtocolFailure(ex, buffer);
            }
        }

        return new DecodeResult(buffer, disconnected: false);
    }

    // 协议错误（计划 8.2 解析第 6 条）：Disconnect 报告并断开；Discard 丢弃剩余缓冲。
    private DecodeResult ProtocolFailure(FrameDecodeException exception, ReadOnlySequence<byte> buffer)
    {
        if (HandleProtocolError(exception))
            return new DecodeResult(buffer, disconnected: true);

        return new DecodeResult(buffer.Slice(buffer.End), disconnected: false);
    }

    // 同步地取出下一帧：flush 为 true 时整段刷新（仅可刷新分帧器调用）。协议错误以异常报告。
    private bool TryProduceFrame(ref ReadOnlySequence<byte> buffer, bool flush, out byte[] frame)
    {
        ReadOnlySequence<byte> sequence;
        bool produced = flush
            ? flushable!.TryFlush(ref buffer, out sequence)
            : decoder.TryDecode(ref buffer, out sequence);

        frame = produced ? sequence.ToArray() : Array.Empty<byte>();
        return produced;
    }

    // 连接丢失的报告与停止由 ChannelStopSequence 执行（与数据报通道共用）。
    private void Fault(DisconnectReason reason, Exception? exception) => stop.Fault(reason, exception);

    // 入站端结束的原因（填充循环记录，解析循环在处理完数据后报告）。
    private sealed class InboundEnd
    {
        public InboundEnd(DisconnectReason reason, Exception? exception)
        {
            Reason = reason;
            Exception = exception;
        }

        public DisconnectReason Reason { get; }

        public Exception? Exception { get; }
    }

    // DecodeAndRouteAsync 的结果：剩余缓冲，以及是否已因协议错误断开。
    private readonly struct DecodeResult
    {
        public DecodeResult(ReadOnlySequence<byte> remaining, bool disconnected)
        {
            Remaining = remaining;
            Disconnected = disconnected;
        }

        public ReadOnlySequence<byte> Remaining { get; }

        public bool Disconnected { get; }
    }

    // 解析循环的静默检查结果：NotDue 继续等待；Flush 整段刷新；Discard 已丢弃未成帧数据；Disconnected 已断开。
    private enum SilenceOutcome
    {
        NotDue,
        Flush,
        Discard,
        Disconnected,
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
}
