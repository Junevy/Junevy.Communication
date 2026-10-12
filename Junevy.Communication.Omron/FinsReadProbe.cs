using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Protocol;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 的心跳探测（计划 Task 13 第 4 条、P4-D15）：读 D0 的 1 个字。
/// 只要拿到结构正确、SID 相符的应答就判为健康——结束码与数据长度都不参与判定，因为设备回了话就说明链路与协议栈正常；
/// FINS/TCP 头的错误码仍按 P4-D14 判为设备侧错误。超时等通道失败原样返回，由通道按 MaxFailures 计数。
/// </summary>
internal sealed class FinsReadProbe : IHealthProbe
{
    // P4-D15：固定读数据内存区第一字；区域代码取自软元件表，与读写请求同源（17.3 节）。
    private const int ProbeWordAddress = 0;
    private const int ProbeWordCount = 1;

    private readonly IByteChannel channel;
    private readonly FinsSession session;

    /// <summary>创建探测。</summary>
    /// <param name="channel">探测所属的字节通道。</param>
    /// <param name="session">提供节点号的会话状态，与通道的握手初始化器共用。</param>
    public FinsReadProbe(IByteChannel channel, FinsSession session)
    {
        this.channel = channel;
        this.session = session;
    }

    /// <inheritdoc />
    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        FinsRoute? route = session.Current.Route;
        if (route is null)
            return CommResult.Fail("The FINS probe has no node address because the handshake has not run on this channel.", CommErrorKind.Unspecified);

        byte sessionId = FinsHead.SessionIds.NextByte();
        byte[] request = FinsMemoryArea.BuildReadRequest(route.Value, sessionId, FinsDeviceTable.DataMemory.WordAreaCode,
                                                         ProbeWordAddress, bitNumber: 0, ProbeWordCount);

        CommResult<byte[]> response = await channel.RequestAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return response.ToResult();

        return FinsMemoryArea.CheckProbeResponse(response.Data!, sessionId);
    }
}
