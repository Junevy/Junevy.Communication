using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// S7 报文外壳：请求侧写 TPKT + COTP DT + Job 头，应答侧校验到应答头并切出参数与数据块。
/// 头字段与长度规则取自计划第 17.1 节说明段；引用号校验按 P4-D8。
/// </summary>
internal static class S7Message
{
    /// <summary>
    /// PDU 引用号的进程级计数器（P4-D17）：本类型是写这个字段的报文头编码器，客户端请求、握手与心跳探测都从这里取号，
    /// 同一通道上不会撞号。取低 16 位。
    /// </summary>
    internal static readonly SequenceCounter PduReferences = new SequenceCounter();

    private const byte ProtocolId = 0x32;
    private const byte RosctrJob = 0x01;
    private const byte RosctrAck = 0x02;
    private const byte RosctrAckData = 0x03;
    private const ushort ReservedWord = 0x0000;

    // TPKT 4 字节 + COTP DT 3 字节 + Job 头 10 字节（协议号、ROSCTR、保留、引用号、参数长度、数据长度）。
    private const int RequestShellBytes = 17;

    /// <summary>
    /// 编码一条请求：TPKT 长度按整帧计算，ROSCTR 为 Job，参数与数据长度取自切片自身（V-S7-3、V-S7-5、V-S7-7、V-S7-9、V-S7-10）。
    /// </summary>
    /// <param name="pduReference">PDU 引用号。</param>
    /// <param name="parameters">参数块。</param>
    /// <param name="data">数据块；没有数据时为空。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildRequest(ushort pduReference, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> data)
    {
        int totalBytes = RequestShellBytes + parameters.Length + data.Length;
        using var buffer = new ByteBufferWriter(totalBytes);
        CotpFrames.WriteTpkt(buffer, totalBytes);
        CotpFrames.WriteDataTransfer(buffer);
        BigEndianFields.WriteUInt8(buffer, ProtocolId);
        BigEndianFields.WriteUInt8(buffer, RosctrJob);
        BigEndianFields.WriteUInt16(buffer, ReservedWord);
        BigEndianFields.WriteUInt16(buffer, pduReference);
        BigEndianFields.WriteUInt16(buffer, checked((ushort)parameters.Length));
        BigEndianFields.WriteUInt16(buffer, checked((ushort)data.Length));
        BigEndianFields.WriteBytes(buffer, parameters);
        BigEndianFields.WriteBytes(buffer, data);
        return buffer.ToArray();
    }

    /// <summary>
    /// 校验并切开一条应答：TPKT、COTP DT、协议号、ROSCTR（2 或 3）、保留字、引用号与两个长度字段。
    /// 错误类与错误码一律取出，是否算失败由 <see cref="S7Errors.CheckHeader"/> 判断。
    /// </summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="expectedReference">请求使用的 PDU 引用号。</param>
    /// <param name="errorClass">应答头错误类；结构不符时为 0。</param>
    /// <param name="errorCode">应答头错误码；结构不符时为 0。</param>
    /// <param name="parameters">参数块；结构不符时为空。</param>
    /// <param name="data">数据块；结构不符时为空。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>帧结构正确返回 true。</returns>
    public static bool TryOpenResponse(
        ReadOnlySpan<byte> frame,
        ushort expectedReference,
        out byte errorClass,
        out byte errorCode,
        out ReadOnlySpan<byte> parameters,
        out ReadOnlySpan<byte> data,
        [NotNullWhen(false)] out string? violation)
    {
        errorClass = S7Errors.NoError;
        errorCode = S7Errors.NoError;
        parameters = ReadOnlySpan<byte>.Empty;
        data = ReadOnlySpan<byte>.Empty;

        if (!CotpFrames.TryUnwrapTpkt(frame, out ReadOnlySpan<byte> content, out violation))
            return false;

        if (!CotpFrames.TryTakeDataTransfer(ref content, out violation))
            return false;

        if (!BigEndianFields.TryTakeUInt8(ref content, out byte protocolId) || protocolId != ProtocolId)
        {
            violation = "The response is not an S7 PDU (protocol id 0x32).";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt8(ref content, out byte rosctr) || rosctr is not (RosctrAck or RosctrAckData))
        {
            violation = $"The response is not an acknowledgement (ROSCTR 0x{rosctr:X2}; expected 0x02 or 0x03).";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt16(ref content, out ushort reserved) || reserved != ReservedWord)
        {
            violation = "The S7 response header reserved word is not 0x0000.";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt16(ref content, out ushort pduReference))
        {
            violation = "The S7 response header carries no PDU reference.";
            return false;
        }

        if (pduReference != expectedReference)
        {
            violation = $"The S7 response reference 0x{pduReference:X4} does not match the request 0x{expectedReference:X4}.";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt16(ref content, out ushort parameterLength)
            || !BigEndianFields.TryTakeUInt16(ref content, out ushort dataLength))
        {
            violation = "The S7 response header carries no parameter or data length.";
            return false;
        }

        // 第 17.1 节说明段：ROSCTR 为 2 或 3 时都带错误类与错误码各 1 字节。
        if (!BigEndianFields.TryTakeUInt8(ref content, out errorClass) || !BigEndianFields.TryTakeUInt8(ref content, out errorCode))
        {
            violation = "The S7 response header carries no error class or error code.";
            return false;
        }

        int declared = parameterLength + dataLength;
        if (content.Length != declared)
        {
            violation = $"The S7 response header declares {declared} byte(s) after the header but the frame carries {content.Length}.";
            return false;
        }

        violation = null;
        parameters = content.Slice(0, parameterLength);
        data = content.Slice(parameterLength, dataLength);
        return true;
    }
}
