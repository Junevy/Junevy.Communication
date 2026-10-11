using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Siemens.Protocol;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的心跳探测（计划第 12 节第 4 条、P4-D15）：读 MB0 的 1 字节。
/// 只要收到结构正确、引用号相符的应答就判为健康——数据项返回码与应答头错误码都不参与判定，
/// 因为设备回了话就说明链路与协议栈正常。超时等通道失败原样返回，由通道按 MaxFailures 计数。
/// </summary>
internal sealed class S7ReadProbe : IHealthProbe
{
    // P4-D15：固定读 M 区第 0 字节的 1 个字节，DB 号对 M 区为 0。
    private const int ProbeByteAddress = 0;
    private const int ProbeByteCount = 1;
    private const int MerkerDbNumber = 0;

    private readonly IByteChannel channel;

    /// <summary>创建探测。</summary>
    /// <param name="channel">探测所属的字节通道。</param>
    public S7ReadProbe(IByteChannel channel)
        => this.channel = channel;

    /// <inheritdoc />
    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        ushort pduReference = S7PduReferences.Next();
        byte[] request = S7ReadVar.BuildRequest(
            pduReference, S7DeviceTable.Merkers.AreaCode, MerkerDbNumber, ProbeByteAddress, ProbeByteCount);

        CommResult<byte[]> response = await channel.RequestAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            return response.ToResult();

        if (!S7Message.TryOpenResponse(response.Data!, pduReference,
                out _, out _, out _, out _, out string? violation))
            return S7Errors.Violation(violation);

        return CommResult.Success();
    }
}
