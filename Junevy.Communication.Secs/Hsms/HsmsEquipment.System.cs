using Junevy.Communication.Secs.Protocol;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Hsms;

/// <summary>
/// 设备端用系统消息（S9）应答收到的消息（计划 Task 6，P51-D13 的 ①）：会话号不对就是 S9F1，
/// 正文解不开就是 S9F7，两种都不交给处理器。
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
        await SendDataAsync(HsmsSystemMessages.UnrecognizedDeviceId(incoming), incoming.SystemBytes, token).ConfigureAwait(false);
        return true;
    }
}