using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec.Protocol;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 的心跳探测（P2 计划 Task 11）：发送回环测试（0619），回显必须与发送的数据一致。
/// 超时等通道失败原样返回，由通道按 MaxFailures 计数。
/// </summary>
internal sealed class McLoopbackProbe : IHealthProbe
{
    // 手册 p.184：回环数据只允许 0–9、A–F。
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("1234ABCD");

    private readonly IByteChannel channel;
    private readonly McFrameExchange exchange;

    /// <summary>创建探测。</summary>
    /// <param name="channel">探测所属的通道。</param>
    /// <param name="settings">选项快照（与客户端的帧类型一致）。</param>
    public McLoopbackProbe(IByteChannel channel, McProtocolSettings settings)
    {
        this.channel = channel;
        exchange = new McFrameExchange(settings);
    }

    /// <inheritdoc />
    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        CommResult<byte[]> response = await channel
            .RequestAsync(exchange.Loopback(Payload), null, cancellationToken)
            .ConfigureAwait(false);
        return response.IsSuccess ? exchange.CheckLoopback(response.Data, Payload) : response.ToResult();
    }
}
