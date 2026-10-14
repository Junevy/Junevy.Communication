using Junevy.Communication.Secs.Protocol;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端拒绝不合规的消息（计划 Task 6，P51-D12、P51-D13 的 ①）：会话号不对回 S9F1，
/// PType 不是 0 回 <c>Reject.req</c> 原因 2；两种都不交给处理器。
/// </summary>
public sealed partial class HsmsEquipment
{
    // ① 会话号不是本设备：S9F1。S9 消息不回应答（SEMI E5），所以只发一条就结束。
    private async Task<bool> AnswerSystemAsync(HsmsMessage incoming, CancellationToken token)
    {
        if (incoming.SessionId == settings.DeviceId)
            return false;

        logger.LogWarning(
            "The host used the device id {SessionId} instead of {DeviceId}; answering S9F1.",
            incoming.SessionId,
            settings.DeviceId);
        await SendNoticeAsync(HsmsSystemMessages.UnrecognizedDeviceId(incoming), token).ConfigureAwait(false);
        return true;
    }

    // 读不出来的帧：PType 不是 0 时回 Reject.req 原因 2（P51-D12），其余只记 Warning 并丢弃——没有可信的头部可以引用。
    private async Task RejectOrDropAsync(byte[] frame, string error, CancellationToken token)
    {
        HsmsMessage? reject = HsmsControl.RejectUnsupportedPType(frame);
        if (reject != null)
            await SendControlAsync(reject, token).ConfigureAwait(false);
        else
            logger.LogWarning("Dropped an HSMS frame that cannot be parsed: {Error}", error);
    }
}