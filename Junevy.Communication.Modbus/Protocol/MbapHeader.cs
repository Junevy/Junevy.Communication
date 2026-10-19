using System.Buffers;
using System.Buffers.Binary;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Modbus.Protocol;

/// <summary>
/// MBAP 头（Modbus TCP 与 Modbus UDP）：事务号 2 字节、协议号 2 字节（恒为 0）、长度 2 字节（站号与 PDU 的字节数）、站号 1 字节。
/// 事务号由进程级计数器分配（P7-D9）；解析时不检查事务号，它由通道按键匹配。
/// </summary>
internal static class MbapHeader
{
    /// <summary>头部的字节数，含站号。</summary>
    public const int HeaderLength = 7;

    private const ushort ProtocolIdentifier = 0;
    private const int LengthFieldEnd = 6;

    /// <summary>进程级事务号计数器：客户端请求与心跳探测共用（新增协议指南 F3）。</summary>
    internal static readonly SequenceCounter TransactionIds = new SequenceCounter();

    /// <summary>写入 MBAP 头。之后的 PDU 由调用方写入同一个 <paramref name="output"/>。</summary>
    /// <param name="output">目标。</param>
    /// <param name="transactionId">事务号。</param>
    /// <param name="unitId">站号。</param>
    /// <param name="pduLength">PDU 的字节数（不含站号）。</param>
    public static void Write(IBufferWriter<byte> output, ushort transactionId, byte unitId, int pduLength)
    {
        BigEndianFields.WriteUInt16(output, transactionId);
        BigEndianFields.WriteUInt16(output, ProtocolIdentifier);
        BigEndianFields.WriteUInt16(output, (ushort)(pduLength + 1));
        BigEndianFields.WriteUInt8(output, unitId);
    }

    /// <summary>
    /// 校验并剥离 MBAP 头，返回 PDU。协议号必须为 0，长度字段必须与帧长一致，站号必须与期望相符（P7-D10）。
    /// </summary>
    /// <param name="frame">完整的 MBAP 帧。</param>
    /// <param name="expectedUnitId">请求的站号。</param>
    /// <returns>PDU 的副本；格式不符返回 <c>ProtocolViolation</c>。</returns>
    public static CommResult<byte[]> Unwrap(ReadOnlySpan<byte> frame, byte expectedUnitId)
    {
        if (frame.Length < HeaderLength)
            return CommResult<byte[]>.Fail("The MBAP frame is shorter than its header.", CommErrorKind.ProtocolViolation);

        ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2));
        if (protocol != ProtocolIdentifier)
            return CommResult<byte[]>.Fail($"The MBAP protocol identifier must be 0, not {protocol}.", CommErrorKind.ProtocolViolation);

        int length = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4));
        int actual = frame.Length - LengthFieldEnd;
        if (length != actual)
        {
            return CommResult<byte[]>.Fail($"The MBAP length field ({length}) does not match the frame ({actual} byte(s) follow it).",
                                           CommErrorKind.ProtocolViolation);
        }

        byte unitId = frame[LengthFieldEnd];
        if (unitId != expectedUnitId)
        {
            return CommResult<byte[]>.Fail($"The response unit ID {unitId} does not match the request unit ID {expectedUnitId}.",
                                           CommErrorKind.ProtocolViolation);
        }

        return CommResult<byte[]>.Success(frame.Slice(HeaderLength).ToArray());
    }
}
