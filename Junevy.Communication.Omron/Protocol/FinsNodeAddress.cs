using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Omron.Protocol;

/// <summary>
/// 节点地址请求与应答（P4-D9、P4-D18）：请求体是 4 字节大端的客户端节点号，0 表示由 PLC 分配；
/// 应答体是客户端与服务端两个节点号，握手结果由 <c>FinsSession</c> 保存（Task 13 第 2 条）。
/// </summary>
internal static class FinsNodeAddress
{
    /// <summary>应答体长度：客户端 4 字节 + 服务端 4 字节（V-F-2）。</summary>
    public const int BodyBytes = FinsFields.UInt32Bytes * 2;

    /// <summary>编码节点地址请求：<paramref name="clientNode"/> 为 0 时即 V-F-1（由 PLC 分配），固定节点号的写法见 P4-D20 ⑥。</summary>
    /// <param name="clientNode">期望使用的客户端节点号；0 表示由 PLC 分配。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildRequest(byte clientNode)
    {
        using var body = new ByteBufferWriter(FinsFields.UInt32Bytes);
        FinsFields.WriteUInt32(body, clientNode);
        return FinsTcpFrame.Build(FinsTcpFrame.CommandNodeRequest, error: 0, body.WrittenSpan);
    }

    /// <summary>解码节点地址应答（V-F-2）：命令必须是 1，体必须是两个 4 字节节点号；头错误码按 P4-D14 先判。</summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <returns>两个节点号；结构错误为 <see cref="CommErrorKind.ProtocolViolation"/>，头错误为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult<FinsNodeNumbers> ReadResponse(ReadOnlySpan<byte> frame)
    {
        if (!FinsTcpFrame.TryUnwrap(frame, out uint command, out uint error, out ReadOnlySpan<byte> body, out string? violation))
            return FinsErrors.Violation<FinsNodeNumbers>(violation);

        CommResult tcp = FinsErrors.CheckTcpError(error);
        if (!tcp.IsSuccess)
            return tcp.As<FinsNodeNumbers>();

        if (command != FinsTcpFrame.CommandNodeResponse)
            return FinsErrors.Violation<FinsNodeNumbers>(
                $"The node address response carries command {command}; expected {FinsTcpFrame.CommandNodeResponse}.");

        if (body.Length != BodyBytes)
            return FinsErrors.Violation<FinsNodeNumbers>(
                $"The node address response carries {body.Length} body byte(s); expected {BodyBytes}.");

        // 体长已校验为 8 字节，两个节点字段一定可读。
        _ = FinsFields.TryTakeUInt32(ref body, out uint clientNumber);
        _ = FinsFields.TryTakeUInt32(ref body, out uint serverNumber);

        // 节点号本身是一个字节（P4-D18）；高位非 0 说明这一帧不是 FINS/TCP 节点应答的形状。
        if (clientNumber > byte.MaxValue || serverNumber > byte.MaxValue)
            return FinsErrors.Violation<FinsNodeNumbers>(
                $"The node address response carries numbers 0x{clientNumber:X8} and 0x{serverNumber:X8}, beyond one byte.");

        return CommResult<FinsNodeNumbers>.Success(new FinsNodeNumbers((byte)clientNumber, (byte)serverNumber));
    }
}

/// <summary>
/// 一次握手的两个节点号：客户端节点号写入后续请求的 SA1，服务端节点号写入 DA1（P4-D18）。
/// </summary>
internal readonly struct FinsNodeNumbers
{
    /// <summary>构造节点号对。</summary>
    /// <param name="clientNode">客户端节点号。</param>
    /// <param name="serverNode">服务端节点号。</param>
    public FinsNodeNumbers(byte clientNode, byte serverNode)
    {
        ClientNode = clientNode;
        ServerNode = serverNode;
    }

    /// <summary>握手得到的客户端节点号。</summary>
    public byte ClientNode { get; }

    /// <summary>握手得到的服务端节点号。</summary>
    public byte ServerNode { get; }
}
