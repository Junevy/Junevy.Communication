using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// 主动端（设备端去连主机）的握手（P51-D11）：发一条 <c>Select.req</c>，超时 T6。
/// </summary>
/// <remarks>
/// <para>
/// <c>Select.rsp</c> 状态为 0 即握手成功；非 0、超时、收到 <c>Reject.req</c>、答非所问或 System Bytes
/// 不对都是握手失败，通道会按 T5（<c>ReconnectOptions</c>）重连，因此这里返回失败结果即可。
/// </para>
/// <para>
/// 握手失败（含取消）一律把状态复位成 <see cref="HsmsState.NotConnected"/>：字节流通了不等于会话可用。
/// </para>
/// </remarks>
internal sealed class HsmsActiveHandshake : IConnectionInitializer
{
    private readonly HsmsSessionState session;
    private readonly HsmsEquipmentTimers timers;
    private readonly ILogger logger;

    /// <summary>创建主动端握手。</summary>
    /// <param name="session">握手成功后写入的状态。</param>
    /// <param name="timers">计时器快照。</param>
    /// <param name="logger">日志记录器。</param>
    internal HsmsActiveHandshake(HsmsSessionState session, HsmsEquipmentTimers timers, ILogger logger)
    {
        this.session = session;
        this.timers = timers;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        session.BeginHandshake("The peer accepted the connection.");
        try
        {
            return await SelectAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 握手窗口到期或连接被关闭：通道不再等本钩子，这里先把状态复位，再把取消原样抛出去。
            session.Reset("The connection went away before the session was selected.");
            throw;
        }
    }

    private async Task<CommResult> SelectAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        var options = new RequestOptions { Timeout = timers.T6 };
        uint systemBytes = HsmsSystemBytes.Next();
        CommResult<byte[]> reply = await channel
            .RequestAsync(HsmsControl.SelectRequest(systemBytes).ToArray(), options, cancellationToken)
            .ConfigureAwait(false);
        if (!reply.IsSuccess)
            return Fail($"Select.req (system bytes {systemBytes}) did not get a reply within {timers.T6} ms: {reply.ErrorMessage}");

        CommResult<HsmsMessage> parsed = HsmsMessage.Read(reply.Data, timers.MaxMessageLength);
        if (!parsed.IsSuccess)
            return Fail(parsed.ErrorMessage!);

        HsmsMessage answer = parsed.Data;
        if (answer.Kind == HsmsControlKind.RejectRequest)
            return Fail($"The peer rejected Select.req with reason {answer.RejectReason}.");
        if (answer.Kind != HsmsControlKind.SelectResponse)
            return Fail($"Select.req was answered with SType {answer.SType} instead of Select.rsp.");
        if (answer.SystemBytes != systemBytes)
            return Fail($"Select.rsp carries system bytes {answer.SystemBytes}, but the request used {systemBytes}.");
        if (answer.Status != HsmsControl.SelectOk)
            return Fail($"Select.rsp reports status {answer.Status}.");

        // Selected 等通道报告 Connected 时再报（见 HsmsSessionState 的类型注释）。
        session.EndHandshake();
        return CommResult.Success();
    }

    // 握手失败对通道是一次打开失败（它会断开并按 T5 重试），对状态是一次复位。
    private CommResult Fail(string reason)
    {
        logger.LogWarning("HSMS active handshake failed: {Reason}.", reason);
        session.Reset("The HSMS handshake failed, so the session is not selected.");
        return CommResult.Fail(reason, CommErrorKind.RemoteError);
    }
}