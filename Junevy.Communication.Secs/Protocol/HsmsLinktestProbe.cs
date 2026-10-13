using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// Linktest 心跳探测（P51-D9）：发 <c>Linktest.req</c>，超时 T6；只在空闲时由通道调度，
/// <c>MaxFailures = 1</c>，因此一次失败就断链重连。
/// </summary>
internal sealed class HsmsLinktestProbe : IHealthProbe
{
    private readonly IByteChannel channel;
    private readonly HsmsEquipmentTimers timers;

    /// <summary>创建探测。</summary>
    /// <param name="channel">绑定到当前连接的字节通道视图。</param>
    /// <param name="timers">计时器快照。</param>
    internal HsmsLinktestProbe(IByteChannel channel, HsmsEquipmentTimers timers)
    {
        this.channel = channel;
        this.timers = timers;
    }

    /// <inheritdoc />
    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        uint systemBytes = HsmsSystemBytes.Next();
        var options = new RequestOptions { Timeout = timers.T6 };
        CommResult<byte[]> reply = await channel
            .RequestAsync(HsmsControl.LinktestRequest(systemBytes).ToArray(), options, cancellationToken)
            .ConfigureAwait(false);
        if (!reply.IsSuccess)
            return reply.ToResult();

        CommResult<HsmsMessage> parsed = HsmsMessage.Read(reply.Data, timers.MaxMessageLength);
        if (!parsed.IsSuccess)
            return parsed.ToResult();

        HsmsMessage answer = parsed.Data;
        return answer.Kind == HsmsControlKind.LinktestResponse
            ? CommResult.Success()
            : CommResult.Fail($"Linktest.req was answered with SType {answer.SType} instead of Linktest.rsp.", CommErrorKind.ProtocolViolation);
    }
}