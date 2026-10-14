using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// 被动端（设备端监听）的握手（P51-D11）：在 T7（<c>SessionHandshakeTimeout</c>）内循环等 <c>Select.req</c>。
/// </summary>
/// <remarks>
/// <para>
/// HSMS-SS 里只有 Active 一方发起 Select，因此被动端"什么都不做地等着"正是通道握手积压
/// （<see cref="IConnectionInitializer"/> 的注释就是这么写的）被设计出来要服务的场景。
/// 循环里能处理的：
/// <list type="bullet">
/// <item><c>Select.req</c> → 回 <c>Select.rsp</c> 状态 0，握手成功；</item>
/// <item><c>Linktest.req</c> → 回 <c>Linktest.rsp</c>，继续等；</item>
/// <item>数据消息 → 回 <c>Reject.req</c> 原因 4（未选中），继续等；</item>
/// <item>PType 不是 0 → 回 <c>Reject.req</c> 原因 2，继续等；</item>
/// <item><c>Separate.req</c> → 不应答，握手失败（断开），与已选中后的处理一致（P51-D12）；</item>
/// <item>其余控制消息 → <c>Reject.req</c> 原因 1，继续等。</item>
/// </list>
/// </para>
/// <para>
/// 这些消息都取不到应答键（P51-D7），因此由 <see cref="IByteChannel.ReceiveAsync"/> 派发到这里，顺序就是到达顺序。
/// T7 由通道的 <c>SessionHandshakeTimeout</c> 兜底：超时会让握手超时并断开，本循环不必自己计时。
/// 握手失败（含取消）一律把状态复位成 <see cref="HsmsState.NotConnected"/>：字节流通了不等于会话可用。
/// </para>
/// </remarks>
internal sealed class HsmsPassiveHandshake : IConnectionInitializer
{
    /// <summary>
    /// 等一帧的最长节奏：握手整体由 T7 约束，这里只是"这一小段时间没等到就再问一次"。
    /// </summary>
    /// <remarks>
    /// 两点都要用它而不是"一次长等"：① <c>ReceiveAsync</c> 不给超时就用通道的
    /// <c>RequestTimeout</c>（TCP 默认 2000 毫秒），比 T7（默认 10000 毫秒）小，主机合法地晚一点 Select 就会被误判；
    /// ② 握手积压与接收等待者的注册之间有一个通道侧的竞态（计划第 20.2 节 F1），
    /// 落在窗口里的帧会被留在积压里等下一次注册，因此短节奏轮询能把它取回来。
    /// </remarks>
    private const int ReceivePollMilliseconds = 200;

    private readonly HsmsSessionState session;
    private readonly HsmsEquipmentTimers timers;
    private readonly ILogger logger;

    /// <summary>创建被动端握手。</summary>
    /// <param name="session">握手成功后写入的状态。</param>
    /// <param name="timers">计时器快照。</param>
    /// <param name="logger">日志记录器。</param>
    internal HsmsPassiveHandshake(HsmsSessionState session, HsmsEquipmentTimers timers, ILogger logger)
    {
        this.session = session;
        this.timers = timers;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        // TCP 连上就进入 NotSelected（P51-D10）：握手钩子开始时报告，早于服务端的 SessionConnected。
        session.MoveTo(HsmsState.NotSelected, "The peer connected.");
        try
        {
            CommResult result = await WaitForSelectAsync(channel, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
                return result;

            session.Reset($"The handshake ended before the peer selected the session: {result.ErrorMessage}");
            return result;
        }
        catch (OperationCanceledException)
        {
            // T7 到期或会话被关闭：通道不再等本钩子，这里先把状态复位，再把取消原样抛出去。
            session.Reset("The connection went away before the peer selected the session.");
            throw;
        }
    }

    // 在 T7 内循环等 Select.req；期间能答的答上，不能答的拒绝，然后继续等。
    private async Task<CommResult> WaitForSelectAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new RequestOptions { Timeout = ReceivePollMilliseconds };
            CommResult<byte[]> received = await channel.ReceiveAsync(options, cancellationToken).ConfigureAwait(false);
            if (!received.IsSuccess)
            {
                // 取消交给上层：通道与握手窗口都会这样结束会话。
                cancellationToken.ThrowIfCancellationRequested();
                if (received.ErrorKind != CommErrorKind.Timeout)
                    return received.ToResult();

                continue;
            }

            CommResult<HsmsMessage> parsed = HsmsMessage.Read(received.Data, timers.MaxMessageLength);
            if (!parsed.IsSuccess)
            {
                CommResult rejected = await RejectOrDropAsync(channel, received.Data, parsed.ErrorMessage, cancellationToken).ConfigureAwait(false);
                if (!rejected.IsSuccess)
                    return rejected;

                continue;
            }

            HsmsMessage incoming = parsed.Data;
            if (incoming.Kind == HsmsControlKind.SelectRequest)
                return await SelectAsync(channel, incoming, cancellationToken).ConfigureAwait(false);

            CommResult answered = await AnswerWhileWaitingAsync(channel, incoming, cancellationToken).ConfigureAwait(false);
            if (!answered.IsSuccess)
                return answered;
        }
    }

    // Select.req 只由 Active 一方发起（P51-D11）：应答状态 0 表示通信建立，之后由派发方接手这条会话。
    private async Task<CommResult> SelectAsync(IByteChannel channel, HsmsMessage request, CancellationToken cancellationToken)
    {
        CommResult sent = await channel
            .SendAsync(HsmsControl.SelectResponse(request.SystemBytes, HsmsControl.SelectOk).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        if (!sent.IsSuccess)
            return sent;

        session.MoveTo(HsmsState.Selected, "Select.rsp 0 (ok) was sent.");
        return CommResult.Success();
    }

    // 读不出来的帧：PType 不是 0 时回 Reject.req 原因 2（SEMI E37，与选中之后一致），其余只记 Warning 并丢弃。
    private async Task<CommResult> RejectOrDropAsync(IByteChannel channel, byte[] frame, string error, CancellationToken cancellationToken)
    {
        HsmsMessage? reject = HsmsControl.RejectUnsupportedPType(frame);
        if (reject != null)
            return await channel.SendAsync(reject.ToArray(), cancellationToken).ConfigureAwait(false);

        logger.LogWarning("Dropped an HSMS frame that cannot be parsed: {Error}", error);
        return CommResult.Success();
    }

    // 还没选中时的应答：能答的答上，不能答的拒绝，然后继续等（P51-D11）。
    private async Task<CommResult> AnswerWhileWaitingAsync(IByteChannel channel, HsmsMessage incoming, CancellationToken cancellationToken)
    {
        if (incoming.Kind == HsmsControlKind.SeparateRequest)
        {
            // Separate 不应答，握手直接失败，通道随即断开。
            logger.LogDebug("The peer sent Separate.req (system bytes {SystemBytes}) before selecting.", incoming.SystemBytes);
            return CommResult.Fail("The peer sent Separate.req before selecting.", CommErrorKind.ConnectionClosed);
        }

        byte[] answer = incoming.Kind switch
        {
            HsmsControlKind.LinktestRequest => HsmsControl.LinktestResponse(incoming.SystemBytes).ToArray(),
            HsmsControlKind.None => HsmsControl.Reject(incoming.SessionId, 0, HsmsRejectReason.NotSelected, incoming.SystemBytes).ToArray(),
            _ => HsmsControl.Reject(HsmsMessage.AnySession, incoming.SType, HsmsRejectReason.NotUnderstood, incoming.SystemBytes).ToArray(),
        };

        logger.LogDebug(
            "HSMS handshake answered {Kind} (system bytes {SystemBytes}) while waiting for Select.req.",
            incoming.Kind,
            incoming.SystemBytes);

        return await channel.SendAsync(answer, cancellationToken).ConfigureAwait(false);
    }
}
