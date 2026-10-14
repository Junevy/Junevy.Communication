using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Protocol;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端发出消息（计划 Task 6，P51-D14、D16）：带 W 位的主消息按 T3 等应答，不带 W 位的只发送。
/// 并发的在途事务由关联键（System Bytes）区分，因此 <see cref="SendAsync"/> 可以并发调用。
/// </summary>
public sealed partial class HsmsEquipment
{
    /// <summary>发一条数据主消息。</summary>
    /// <param name="primary">要发的消息；必须是主消息（奇数 Function）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果：带 W 位时成功则 <see cref="SecsResult.Reply"/> 非 null。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="primary"/> 为 null。</exception>
    public async Task<SecsResult> SendAsync(SecsMessage primary, CancellationToken cancellationToken = default)
    {
        if (primary == null)
            throw new ArgumentNullException(nameof(primary));
        if (HsmsMessage.IsReplyFunction(primary.Function))
        {
            return SecsResult.Failure(
                SecsError.InvalidRequest,
                $"The function {primary.Function} is a reply; use the handler of a primary to send replies.");
        }

        lock (sync)
        {
            if (disposed || !started)
                return SecsResult.Failure(SecsError.ConnectionLost, "The equipment is not running.");
        }

        // 没选中就不发（P51-D14）：字节流通了不等于会话可用。
        if (session.State != HsmsState.Selected)
        {
            return SecsResult.Failure(SecsError.NotSelected, $"The session is {session.State}, so the primary is not sent.");
        }

        IByteChannel? channel = CurrentChannel();
        if (channel == null)
            return SecsResult.Failure(SecsError.ConnectionLost, "There is no connected session.");

        uint systemBytes = HsmsSystemBytes.Next();
        byte[] frame = HsmsMessage.Write(primary, settings.DeviceId, systemBytes);
        logger.LogDebug(
            "HSMS TX stream {Stream} function {Function} (reply expected {ReplyExpected}, system bytes {SystemBytes}).",
            primary.Stream,
            primary.Function,
            primary.ReplyExpected,
            systemBytes);

        if (!primary.ReplyExpected)
        {
            CommResult sent = await channel.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            return sent.IsSuccess ? SecsResult.Success(null) : FailureOf(sent, "The primary could not be sent.");
        }

        var options = new RequestOptions { Timeout = settings.Timers.T3 };
        CommResult<byte[]> reply = await channel.RequestAsync(frame, options, cancellationToken).ConfigureAwait(false);
        return await TranslateReplyAsync(primary, systemBytes, reply, cancellationToken).ConfigureAwait(false);
    }

    // 应答的四种结局：正常、Abort（SxF0）、Reject.req、T3 超时；另外是"应答不合法"与"链路没了"。
    private async Task<SecsResult> TranslateReplyAsync(SecsMessage primary, uint systemBytes, CommResult<byte[]> reply, CancellationToken cancellationToken)
    {
        if (!reply.IsSuccess)
        {
            if (reply.ErrorKind == CommErrorKind.Timeout)
            {
                // T3 是事务失败，不是链路失败：先告诉对方我方在等什么（P51-D14）。
                await SendNoticeAsync(HsmsSystemMessages.LinktestTimeout(HeaderOf(primary, systemBytes)), cancellationToken).ConfigureAwait(false);
                return SecsResult.Failure(SecsError.ReplyTimeout, $"No reply within {settings.Timers.T3} ms.");
            }

            return FailureOf(reply.ToResult(), "The reply could not be received.");
        }

        CommResult<HsmsMessage> parsed = HsmsMessage.Read(reply.Data, settings.Timers.MaxMessageLength);
        if (!parsed.IsSuccess)
        {
            // 连头部都读不出来时拿不到它的 10 个字节，只能引用我方主消息的头。
            logger.LogWarning("The reply cannot be parsed: {Error}", parsed.ErrorMessage);
            await SendNoticeAsync(HsmsSystemMessages.IllegalData(HeaderOf(primary, systemBytes)), cancellationToken).ConfigureAwait(false);
            return SecsResult.Failure(SecsError.InvalidReply, "The reply cannot be parsed.");
        }

        return await AnswerOf(primary, parsed.Data, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SecsResult> AnswerOf(SecsMessage primary, HsmsMessage answer, CancellationToken cancellationToken)
    {
        if (answer.Kind == HsmsControlKind.RejectRequest)
        {
            return SecsResult.Failure(
                SecsError.Rejected,
                $"The host rejected the primary with reason {answer.RejectReason}.",
                answer.RejectReason);
        }

        if (answer.Function == 0)
            return SecsResult.Failure(SecsError.Aborted, "The host aborted the transaction with SxF0.");

        string? mismatch = MismatchOf(primary, answer.Stream, answer.Function, answer.ReplyExpected);
        if (mismatch != null)
        {
            logger.LogWarning("The reply does not match the primary: {Mismatch}.", mismatch);
            await SendNoticeAsync(HsmsSystemMessages.IllegalData(answer), cancellationToken).ConfigureAwait(false);
            return SecsResult.Failure(SecsError.InvalidReply, $"The reply does not match the primary: {mismatch}.");
        }

        // 应答可以没有正文（SEMI E5 的 S1F2 就常常没有），有正文时必须能解码。
        SecsItem? body = null;
        if (answer.Body.Length > 0)
        {
            CommResult<SecsItem> decoded = SecsItemCodec.Decode(answer.Body, settings.Timers.MaxMessageLength);
            if (!decoded.IsSuccess)
            {
                logger.LogWarning("The body of the reply cannot be decoded: {Error}", decoded.ErrorMessage);
                await SendNoticeAsync(HsmsSystemMessages.IllegalData(answer), cancellationToken).ConfigureAwait(false);
                return SecsResult.Failure(SecsError.InvalidReply, "The body of the reply cannot be decoded.");
            }

            body = decoded.Data;
        }

        return SecsResult.Success(SecsMessage.Create(primary.Stream, (byte)(primary.Function + 1), replyExpected: false, body));
    }

    // 通道失败到 SECS 失败的翻译：连接类与取消各有明确的语义，其余归为未指定。
    private SecsResult FailureOf(CommResult failure, string prefix) => failure.ErrorKind switch
    {
        CommErrorKind.NotConnected or CommErrorKind.ConnectionClosed =>
            SecsResult.Failure(SecsError.ConnectionLost, $"{prefix} {failure.ErrorMessage}"),
        CommErrorKind.Cancelled => SecsResult.Failure(SecsError.Cancelled, $"{prefix} {failure.ErrorMessage}"),
        _ => SecsResult.Failure(SecsError.Unspecified, $"{prefix} {failure.ErrorMessage}"),
    };

    private IByteChannel? CurrentChannel()
    {
        lock (sync)
        {
            return current;
        }
    }

    // S9 消息要引用"我方主消息的头"，因此把这个头当报文写一遍再读回来（只在出错路径上做一次）。
    private HsmsMessage HeaderOf(SecsMessage primary, uint systemBytes)
    {
        byte[] frame = HsmsMessage.Write(primary, settings.DeviceId, systemBytes);
        var parsed = HsmsMessage.Read(frame, settings.Timers.MaxMessageLength);
        return parsed.IsSuccess
            ? parsed.Data
            : throw new InvalidOperationException($"The header of the primary cannot be read back: {parsed.ErrorMessage}");
    }

    // S9 消息是我方新发起的主消息（不带 W 位）：取新的 System Bytes，不能沿用被涉及消息的号——
    // 那样主机会把它当成那条主消息的应答（第 17.1 节 V-H-14、V-H-16、V-H-17、V-H-18）。
    private Task SendNoticeAsync(SecsMessage notice, CancellationToken cancellationToken)
        => SendDataAsync(notice, HsmsSystemBytes.Next(), cancellationToken);

    private async Task SendDataAsync(SecsMessage message, uint systemBytes, CancellationToken cancellationToken)
    {
        IByteChannel? channel = CurrentChannel();
        if (channel == null)
        {
            logger.LogDebug("There is no connection, so the answer is not sent.");
            return;
        }

        CommResult sent = await channel
            .SendAsync(HsmsMessage.Write(message, settings.DeviceId, systemBytes), cancellationToken)
            .ConfigureAwait(false);
        if (!sent.IsSuccess)
            logger.LogWarning("Sending an answer failed: {Error}", sent.ErrorMessage);
    }

    private async Task SendControlAsync(HsmsMessage control, CancellationToken cancellationToken)
    {
        IByteChannel? channel = CurrentChannel();
        if (channel == null)
        {
            logger.LogDebug("There is no connection, so the control answer is not sent.");
            return;
        }

        CommResult sent = await channel.SendAsync(control.ToArray(), cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
            logger.LogWarning("Sending a control answer failed: {Error}", sent.ErrorMessage);
    }

    // 应答的形状：同一 Stream、Function + 1、不带 W 位。派发与发送两条路径共用这条规则。
    private static string? MismatchOf(SecsMessage primary, byte stream, byte function, bool replyExpected)
    {
        if (stream != primary.Stream)
            return $"the reply is on stream {stream}";
        if (function != primary.Function + 1)
            return $"the reply is function {function}, expected {primary.Function + 1}";
        if (replyExpected)
            return "the reply asks for a reply of its own";

        return null;
    }
}
