using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Protocol;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 的节点地址握手（计划 Task 13 第 2 条、P4-D9、P4-D18）：一帧节点地址请求，应答的两个节点号连同选项里的
/// 网络号与单元号合成路由写入会话。失败都返回带原因的结果而不抛异常：帧结构为 <see cref="CommErrorKind.ProtocolViolation"/>、
/// 头错误码为 <see cref="CommErrorKind.RemoteError"/>、超时与断开等通道失败原样返回，且失败时不写会话。
/// 请求不带单次选项，因此时限就是通道的请求超时，握手自身不再设时限。
/// </summary>
internal sealed class FinsNodeHandshake : IConnectionInitializer
{
    private readonly FinsProtocolSettings settings;
    private readonly FinsSession session;

    /// <summary>创建握手。</summary>
    /// <param name="settings">连接参数快照。</param>
    /// <param name="session">握手结果写入的会话状态。</param>
    public FinsNodeHandshake(FinsProtocolSettings settings, FinsSession session)
    {
        this.settings = settings;
        this.session = session;
    }

    /// <inheritdoc />
    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        byte[] request = FinsNodeAddress.BuildRequest(settings.ClientNode);
        CommResult<byte[]> reply = await channel.RequestAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (!reply.IsSuccess)
            return reply.ToResult();

        CommResult<FinsNodeNumbers> nodes = FinsNodeAddress.ReadResponse(reply.Data!);
        if (!nodes.IsSuccess)
            return nodes.ToResult();

        // 以应答为准：请求固定节点号时 PLC 也可能另派一个号，后续 SA1 用应答里的那个（P4-D20 ⑥）。
        session.ReplaceAfterHandshake(settings.RouteWith(nodes.Data!));
        return CommResult.Success();
    }
}
