using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Tcp;
using Junevy.Communication.Tcp.Server;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端收到消息的派发（计划 Task 6，P51-D12、D13、D16）：控制消息按 E37 的规则应答，数据主消息
/// 按 S9 消息的顺序处理，再交给处理器。所有消息按到达顺序逐个处理，同一连接上不并发。
/// </summary>
public sealed partial class HsmsEquipment
{
    /// <summary>当前执行流是否正在派发消息：处理器里停止时靠它避免等自己（P51-D19）。</summary>
    private static readonly AsyncLocal<bool> inDispatch = new AsyncLocal<bool>();

    /// <summary>每秒一条的 Debug 日志不带正文（计划第 0 节 P51-D16）：这里只记方向、S/F、W 位与 System Bytes。</summary>
    private void OnSessionConnected(object? sender, TcpSessionEventArgs args)
    {
        lock (sync)
        {
            current = args.Session;
        }

        args.Session.FrameReceived += OnFrameReceived;
        logger.LogDebug("HSMS session {Session} connected from {Remote}.", args.Session.Id, args.Session.RemoteEndPoint);
    }

    // 连接断了即回到 NotConnected（P51-D10）；正在等应答的请求由通道以失败结果结束，不会泄漏。
    private void OnSessionClosed(object? sender, TcpSessionClosedEventArgs args)
    {
        args.Session.FrameReceived -= OnFrameReceived;
        lock (sync)
        {
            if (ReferenceEquals(current, args.Session))
                current = null;
        }

        session.Reset($"The session {args.Session.Id} closed ({args.Reason}).");
    }

    // 主动端：任何"不再是 Connected"的状态（断开、重连中、已释放）都意味着会话不可用（P51-D10）。
    // Connected 时状态由握手钩子推进（NotSelected → Selected），这里只挂上当前连接。
    private void OnClientStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        lock (sync)
        {
            current = args.CurrentState == ConnectionState.Connected ? sender as IByteChannel : null;
            if (current != null || args.CurrentState == ConnectionState.Connected)
                return;
        }

        session.Reset($"The channel changed to {args.CurrentState} ({args.Reason}).");
    }

    // 通道的帧事件是"逐个隔离异常地调用"而不是等待，因此这里自己把消息串起来：
    // P51-D13 要求按到达顺序逐个处理，处理器可以慢，但不能与下一条并行。
    private void OnFrameReceived(object? sender, FrameReceivedEventArgs args)
    {
        lock (dispatchGate)
        {
            dispatchTail = dispatchTail
                .ContinueWith(_ => DispatchAsync(args.Data), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
        }
    }

    // 停止时等在途的派发跑完（P51-D19：处理器必须响应令牌，因此这一等是有界的）。
    private async Task DrainDispatchAsync()
    {
        Task pending;
        lock (dispatchGate)
        {
            pending = dispatchTail;
        }

        await pending.ConfigureAwait(false);
    }

    // 入口：解析失败只记 Warning 并丢弃（P51-D13 的 ② 之后没有可用的头部，S9 消息无法引用它）。
    private async Task DispatchAsync(byte[] frame)
    {
        // 派发上下文：处理器里调用 StopAsync 时靠它认出"自己在派发里"，从而不等自己（P51-D19）。
        inDispatch.Value = true;
        try
        {
            await DispatchFrameAsync(frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 处理器与通道的异常都不能终止派发循环：记 Error 之后继续处理后面的消息。
            logger.LogError(ex, "Dispatching an HSMS frame failed.");
        }
        finally
        {
            inDispatch.Value = false;
        }
    }

    private async Task DispatchFrameAsync(byte[] frame)
    {
        CommResult<HsmsMessage> parsed = HsmsMessage.Read(frame, settings.Timers.MaxMessageLength);
        if (!parsed.IsSuccess)
        {
            logger.LogWarning("Dropped an HSMS frame that cannot be parsed: {Error}", parsed.ErrorMessage);
            return;
        }

        HsmsMessage incoming = parsed.Data;
        logger.LogDebug(
            "HSMS RX {Kind} stream {Stream} function {Function} (reply expected {ReplyExpected}, system bytes {SystemBytes}).",
            incoming.Kind,
            incoming.Stream,
            incoming.Function,
            incoming.ReplyExpected,
            incoming.SystemBytes);

        CancellationToken token = HandlerToken();
        if (incoming.Kind == HsmsControlKind.None)
            await HandlePrimaryAsync(incoming, token).ConfigureAwait(false);
        else
            await HandleControlAsync(incoming, token).ConfigureAwait(false);
    }

    // P51-D12：已选中之后收到的控制消息。
    private async Task HandleControlAsync(HsmsMessage incoming, CancellationToken token)
    {
        switch (incoming.Kind)
        {
            case HsmsControlKind.LinktestRequest:
                await SendControlAsync(HsmsControl.LinktestResponse(incoming.SystemBytes), token).ConfigureAwait(false);
                return;

            case HsmsControlKind.SelectRequest:
                await SendControlAsync(HsmsControl.SelectResponse(incoming.SystemBytes, HsmsControl.SelectAlreadyActive), token).ConfigureAwait(false);
                return;

            case HsmsControlKind.DeselectRequest:
                await SendControlAsync(Reject(incoming, HsmsRejectReason.NotUnderstood), token).ConfigureAwait(false);
                return;

            case HsmsControlKind.SeparateRequest:
                await CloseCurrentAsync().ConfigureAwait(false);
                return;

            // *.rsp 与 Reject.req 只有在有对应在途事务时才会被关联表认领，因此到这里的一定是无主的：原因 3。
            case HsmsControlKind.SelectResponse:
            case HsmsControlKind.DeselectResponse:
            case HsmsControlKind.LinktestResponse:
            case HsmsControlKind.RejectRequest:
                await SendControlAsync(Reject(incoming, HsmsRejectReason.TransactionNotOpen), token).ConfigureAwait(false);
                return;

            default:
                await SendControlAsync(Reject(incoming, HsmsRejectReason.NotUnderstood), token).ConfigureAwait(false);
                return;
        }
    }

    // P51-D13：设备端处理收到的数据主消息，顺序是 ① 会话号 ② 正文 ③ 处理器 ④ 调用处理器。
    private async Task HandlePrimaryAsync(HsmsMessage incoming, CancellationToken token)
    {
        // 迟到或无主的应答（偶数 Function 取应答键但没有在途事务）：记 Warning 并丢弃。
        if (HsmsMessage.IsReplyFunction(incoming.Function))
        {
            logger.LogWarning(
                "Dropped an unsolicited data reply (stream {Stream}, function {Function}, system bytes {SystemBytes}); no request is in flight.",
                incoming.Stream,
                incoming.Function,
                incoming.SystemBytes);
            return;
        }

        // ① 会话号不是本设备：S9F1，不回应答（SEMI E5）。
        if (await AnswerSystemAsync(incoming, token).ConfigureAwait(false))
            return;

        // ② 正文解码失败：S9F7。没有正文是合法的（SEMI E5 的 S1F1 W 就常常没有正文）。
        SecsItem? body = null;
        if (incoming.Body.Length > 0)
        {
            CommResult<SecsItem> decoded = SecsItemCodec.Decode(incoming.Body, settings.Timers.MaxMessageLength);
            if (!decoded.IsSuccess)
            {
                logger.LogWarning("The body of the received primary cannot be decoded: {Error}", decoded.ErrorMessage);
                await SendDataAsync(HsmsSystemMessages.IllegalData(incoming), incoming.SystemBytes, token).ConfigureAwait(false);
                return;
            }

            body = decoded.Data;
        }

        var primary = SecsMessage.Create(incoming.Stream, incoming.Function, incoming.ReplyExpected, body);
        if (!handlers.TryGet(primary.Stream, primary.Function, out SecsMessageHandler handler))
        {
            SecsMessage unhandled = handlers.HasStream(primary.Stream)
                ? HsmsSystemMessages.UnknownFunction(incoming)
                : HsmsSystemMessages.UnknownStream(incoming);
            await SendDataAsync(unhandled, incoming.SystemBytes, token).ConfigureAwait(false);
            return;
        }

        SecsMessage? reply = await InvokeAsync(handler, primary, token).ConfigureAwait(false);
        if (token.IsCancellationRequested)
        {
            logger.LogDebug("The equipment is stopping; the reply of the handler is not sent.");
            return;
        }

        await AnswerAsync(primary, incoming, reply, token).ConfigureAwait(false);
    }

    // ④ 调用处理器：异常、null 与不匹配的应答都要中止事务（SxF0），因此分开记三条 Error。
    private async Task<SecsMessage?> InvokeAsync(SecsMessageHandler handler, SecsMessage primary, CancellationToken token)
    {
        try
        {
            return await handler(primary, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "The handler threw while processing stream {Stream} function {Function}; the transaction is aborted.",
                primary.Stream,
                primary.Function);
            return null;
        }
    }

    private async Task AnswerAsync(SecsMessage primary, HsmsMessage incoming, SecsMessage? reply, CancellationToken token)
    {
        if (!primary.ReplyExpected)
        {
            if (reply != null)
            {
                logger.LogWarning(
                    "The handler returned a reply for a primary without the W bit (stream {Stream}, function {Function}); it is ignored.",
                    primary.Stream,
                    primary.Function);
            }

            return;
        }

        if (reply == null)
        {
            logger.LogError(
                "The handler returned no reply for stream {Stream} function {Function}; the transaction is aborted.",
                primary.Stream,
                primary.Function);
            await SendDataAsync(HsmsSystemMessages.Abort(primary.Stream), incoming.SystemBytes, token).ConfigureAwait(false);
            return;
        }

        string? mismatch = MismatchOf(primary, reply.Stream, reply.Function, reply.ReplyExpected);
        if (mismatch != null)
        {
            logger.LogError(
                "The handler returned a reply that does not match the primary (stream {Stream} function {Function}): {Mismatch}.",
                primary.Stream,
                primary.Function,
                mismatch);
            await SendDataAsync(HsmsSystemMessages.Abort(primary.Stream), incoming.SystemBytes, token).ConfigureAwait(false);
            return;
        }

        // 应答原样回送主消息的 System Bytes（P51-D6）。
        await SendDataAsync(reply, incoming.SystemBytes, token).ConfigureAwait(false);
    }

    private HsmsMessage Reject(HsmsMessage incoming, HsmsRejectReason reason)
        => HsmsControl.Reject(HsmsMessage.AnySession, incoming.SType, reason, incoming.SystemBytes);

    // Separate.req 不应答，立即断开（P51-D12）。被动端关会话，主动端断开通道。
    private async Task CloseCurrentAsync()
    {
        IByteChannel? attached;
        lock (sync)
        {
            attached = current;
        }

        if (attached is IConnectable client)
            await client.DisconnectAsync().ConfigureAwait(false);
        else if (attached is ITcpSession session)
            await session.CloseAsync().ConfigureAwait(false);
    }

    // 处理器拿到的令牌：停止或释放时取消（P51-D19）。停止之后还没有开始过时用默认令牌。
    private CancellationToken HandlerToken()
    {
        lock (sync)
        {
            return running?.Token ?? default;
        }
    }
}
