using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Siemens.Protocol;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的握手（计划第 12 节第 3 条、P4-D9）：COTP CR → CC，再 Setup Communication 协商 PDU 长度并写入会话。
/// 每一步失败都返回带原因的结果而不抛异常：帧结构为 <see cref="CommErrorKind.ProtocolViolation"/>、
/// 应答头错误为 <see cref="CommErrorKind.RemoteError"/>、超时与断开等通道失败原样返回。
/// 两步请求都不带单次选项，因此每一步的时限就是通道的请求超时，握手自身不再设时限。
/// </summary>
internal sealed class S7Handshake : IConnectionInitializer
{
    private readonly S7ProtocolSettings settings;
    private readonly S7Session session;

    /// <summary>创建握手。</summary>
    /// <param name="settings">连接参数快照。</param>
    /// <param name="session">握手结果写入的会话状态。</param>
    public S7Handshake(S7ProtocolSettings settings, S7Session session)
    {
        this.settings = settings;
        this.session = session;
    }

    /// <inheritdoc />
    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        byte[] request = CotpFrames.BuildConnectionRequest(settings.ConnectionType, settings.Rack, settings.Slot);
        CommResult<byte[]> confirm = await channel.RequestAsync(request, null, cancellationToken).ConfigureAwait(false);
        if (!confirm.IsSuccess)
            return confirm.ToResult();

        CommResult connectionConfirm = CotpFrames.ReadConnectionConfirm(confirm.Data!);
        if (!connectionConfirm.IsSuccess)
            return connectionConfirm;

        ushort pduReference = S7Message.PduReferences.NextUInt16();
        CommResult<byte[]> setup = await channel
            .RequestAsync(S7SetupCommunication.BuildRequest(pduReference), null, cancellationToken)
            .ConfigureAwait(false);
        if (!setup.IsSuccess)
            return setup.ToResult();

        CommResult<int> negotiated = S7SetupCommunication.ReadNegotiatedPdu(setup.Data!, pduReference);
        if (!negotiated.IsSuccess)
            return negotiated.ToResult();

        session.ReplaceAfterHandshake(negotiated.Data);
        return CommResult.Success();
    }
}
