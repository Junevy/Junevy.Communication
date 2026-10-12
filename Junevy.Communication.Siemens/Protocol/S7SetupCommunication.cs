using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// Setup Communication（计划第 17.1 节 V-S7-3、V-S7-4）：请求固定 960 字节 PDU（P4-D13），
/// 应答取参数末尾 2 字节的协商值。
/// </summary>
internal static class S7SetupCommunication
{
    /// <summary>P4-D13：请求的 PDU 长度固定 960 字节。</summary>
    public const int RequestedPduBytes = 960;

    /// <summary>P4-D13：协商结果小于该值时视为帧结构错误，握手失败。</summary>
    public const int MinimumNegotiatedPduBytes = 64;

    private const byte ServiceId = 0xF0;
    private const byte ReservedByte = 0x00;

    // V-S7-3 中协商值之前的 4 个字节：主叫与被叫各自允许的最大并行作业数（Max AmQ calling / called）。
    // 通道按 Sequential 串行发送，同一时刻只有一个作业，因此两者都请求 1。
    private const ushort MaxAmqCalling = 0x0001;
    private const ushort MaxAmqCalled = 0x0001;

    // 参数块共 8 字节，最后 2 字节是 PDU 长度（V-S7-4）。
    private const int ParameterBytes = 8;

    /// <summary>编码 Setup Communication 请求（V-S7-3）。</summary>
    /// <param name="pduReference">PDU 引用号。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildRequest(ushort pduReference)
    {
        using var parameters = new ByteBufferWriter(ParameterBytes);
        BigEndianFields.WriteUInt8(parameters, ServiceId);
        BigEndianFields.WriteUInt8(parameters, ReservedByte);
        BigEndianFields.WriteUInt16(parameters, MaxAmqCalling);
        BigEndianFields.WriteUInt16(parameters, MaxAmqCalled);
        BigEndianFields.WriteUInt16(parameters, checked((ushort)RequestedPduBytes));
        return S7Message.BuildRequest(pduReference, parameters.WrittenSpan, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>解码 Setup 应答（V-S7-4）：先按 P4-D14 检查应答头，再校验参数形状并取出协商的 PDU 长度。</summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="expectedReference">请求使用的 PDU 引用号。</param>
    /// <returns>协商的 PDU 字节数；结构错误为 <see cref="CommErrorKind.ProtocolViolation"/>，应答头有错误时为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult<int> ReadNegotiatedPdu(ReadOnlySpan<byte> frame, ushort expectedReference)
    {
        if (!S7Message.TryOpenResponse(frame, expectedReference, out byte errorClass, out byte errorCode,
                out ReadOnlySpan<byte> parameters, out _, out string? violation))
            return S7Errors.Violation<int>(violation);

        CommResult header = S7Errors.CheckHeader(errorClass, errorCode);
        if (!header.IsSuccess)
            return header.As<int>();

        if (parameters.Length != ParameterBytes)
            return S7Errors.Violation<int>($"The setup response carries {parameters.Length} parameter byte(s); expected {ParameterBytes}.");

        if (parameters[0] != ServiceId)
            return S7Errors.Violation<int>($"The setup response carries service 0x{parameters[0]:X2}; expected 0x{ServiceId:X2}.");

        // 参数长度已校验为 8 字节，末尾 2 字节一定可读。
        ReadOnlySpan<byte> tail = parameters.Slice(ParameterBytes - BigEndianFields.UInt16Bytes);
        _ = BigEndianFields.TryTakeUInt16(ref tail, out ushort negotiated);

        // P4-D13：协商值过小时继续通信会写出超过 PDU 的字节，握手阶段就拒绝。
        if (negotiated < MinimumNegotiatedPduBytes)
            return S7Errors.Violation<int>(
                $"The negotiated PDU size {negotiated} is below the minimum {MinimumNegotiatedPduBytes} (P4-D13).");

        return CommResult<int>.Success(negotiated);
    }
}
