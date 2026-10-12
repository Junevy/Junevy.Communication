using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// TPKT（RFC 1006）与 COTP（ISO 8602-3）层：连接请求 CR、连接确认 CC、数据传输 DT 与 TPKT 长度字段的编码与校验。
/// 字节取自计划第 17.1 节 V-S7-1 与 V-S7-2；TSAP 按 P4-D19 计算。
/// </summary>
internal static class CotpFrames
{
    private const int TpktHeaderBytes = 4;
    private const byte TpktVersion = 0x03;
    private const byte TpktReserved = 0x00;

    private const byte CrCode = 0xE0;
    private const byte CcCode = 0xD0;
    private const byte DtCode = 0xF0;
    private const byte DtCredit = 0x80;
    private const byte DtLengthBytes = 0x02;

    // V-S7-1：长度字段的值为 17（不含长度字节自身），整帧 22 字节。
    private const byte CrLengthBytes = 17;
    private const ushort CrDestinationReference = 0x0000;
    private const ushort CrSourceReference = 0x0001;
    private const byte CrClass = 0x00;
    private const byte ProtocolClassOption = 0xC0;
    private const byte ProtocolClassLength = 0x01;
    private const byte ProtocolClassValue = 0x0A;

    // 选项 C1 是主叫（本地）TSAP，C2 是被叫（远端）TSAP：V-S7-1 中 C1 为 0x0100、C2 为 0x0101。
    private const byte LocalTsapOption = 0xC1;
    private const byte RemoteTsapOption = 0xC2;
    private const byte TsapLength = 0x02;
    private const ushort LocalTsap = 0x0100;

    // P4-D19：远端 TSAP = 连接类型 << 8 | Rack << 5 | 槽号。
    private const int ConnectionTypeShift = 8;
    private const int RackShift = 5;

    /// <summary>
    /// 编码 COTP CR 并套上 TPKT（V-S7-1）。连接类型、Rack 与槽号的取值范围由调用方校验（P4-D19：类型 1–3、Rack 0–7、槽 0–31）。
    /// </summary>
    /// <param name="connectionType">连接类型。</param>
    /// <param name="rack">Rack 号。</param>
    /// <param name="slot">槽号。</param>
    /// <returns>完整的 CR 帧。</returns>
    public static byte[] BuildConnectionRequest(int connectionType, int rack, int slot)
    {
        const int TotalBytes = TpktHeaderBytes + BigEndianFields.UInt8Bytes + CrLengthBytes;
        using var buffer = new ByteBufferWriter(TotalBytes);
        WriteTpkt(buffer, TotalBytes);
        BigEndianFields.WriteUInt8(buffer, CrLengthBytes);
        BigEndianFields.WriteUInt8(buffer, CrCode);
        BigEndianFields.WriteUInt16(buffer, CrDestinationReference);
        BigEndianFields.WriteUInt16(buffer, CrSourceReference);
        BigEndianFields.WriteUInt8(buffer, CrClass);
        BigEndianFields.WriteUInt8(buffer, ProtocolClassOption);
        BigEndianFields.WriteUInt8(buffer, ProtocolClassLength);
        BigEndianFields.WriteUInt8(buffer, ProtocolClassValue);
        BigEndianFields.WriteUInt8(buffer, LocalTsapOption);
        BigEndianFields.WriteUInt8(buffer, TsapLength);
        BigEndianFields.WriteUInt16(buffer, LocalTsap);
        BigEndianFields.WriteUInt8(buffer, RemoteTsapOption);
        BigEndianFields.WriteUInt8(buffer, TsapLength);
        BigEndianFields.WriteUInt16(buffer, RemoteTsap(connectionType, rack, slot));
        return buffer.ToArray();
    }

    /// <summary>校验连接确认（V-S7-2）：TPKT 正确、PDU 类型为 0xD0。参数不解析（计划 Task 7 第 2 条）。</summary>
    /// <param name="frame">完整的 CC 帧。</param>
    /// <returns>结构错误为 <see cref="CommErrorKind.ProtocolViolation"/>。</returns>
    public static CommResult ReadConnectionConfirm(ReadOnlySpan<byte> frame)
    {
        if (!TryUnwrapTpkt(frame, out ReadOnlySpan<byte> content, out string? violation))
            return S7Errors.Violation(violation);

        if (!BigEndianFields.TryTakeUInt8(ref content, out byte lengthBytes) || lengthBytes < BigEndianFields.UInt8Bytes)
            return S7Errors.Violation("The COTP connection confirm carries no PDU type.");

        if (!BigEndianFields.TryTakeUInt8(ref content, out byte pduType) || pduType != CcCode)
            return S7Errors.Violation("The COTP response is not a connection confirm (PDU type 0xD0).");

        return CommResult.Success();
    }

    /// <summary>写入 TPKT：<c>03 00</c> 加大端的总长度，长度含 TPKT 自身（第 17.1 节说明段）。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="totalBytes">整帧字节数。</param>
    public static void WriteTpkt(IBufferWriter<byte> buffer, int totalBytes)
    {
        BigEndianFields.WriteUInt8(buffer, TpktVersion);
        BigEndianFields.WriteUInt8(buffer, TpktReserved);
        BigEndianFields.WriteUInt16(buffer, checked((ushort)totalBytes));
    }

    /// <summary>写入 COTP DT 头 <c>02 F0 80</c>（第 17.1 节说明段）。</summary>
    /// <param name="buffer">目标缓冲。</param>
    public static void WriteDataTransfer(IBufferWriter<byte> buffer)
    {
        BigEndianFields.WriteUInt8(buffer, DtLengthBytes);
        BigEndianFields.WriteUInt8(buffer, DtCode);
        BigEndianFields.WriteUInt8(buffer, DtCredit);
    }

    /// <summary>校验 TPKT 并切出 COTP 协议数据：版本 3、保留字节 0、长度字段等于整帧长度。</summary>
    /// <param name="frame">完整的帧。</param>
    /// <param name="content">TPKT 之后的字节；失败时为空。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>结构正确返回 true。</returns>
    public static bool TryUnwrapTpkt(
        ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> content, [NotNullWhen(false)] out string? violation)
    {
        content = ReadOnlySpan<byte>.Empty;
        ReadOnlySpan<byte> rest = frame;
        if (!BigEndianFields.TryTakeUInt8(ref rest, out byte version) || version != TpktVersion)
        {
            violation = "The response does not start with TPKT version 3.";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt8(ref rest, out byte reserved) || reserved != TpktReserved)
        {
            violation = "The TPKT reserved byte is not 0.";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt16(ref rest, out ushort totalBytes))
        {
            violation = "The TPKT carries no length field.";
            return false;
        }

        if (totalBytes != frame.Length)
        {
            violation = $"The TPKT length {totalBytes} does not match the frame length {frame.Length}.";
            return false;
        }

        violation = null;
        content = rest;
        return true;
    }

    /// <summary>
    /// 切出 COTP DT 头：长度 2、PDU 类型 0xF0。信用字节的值不参与判断（17.1 节的应答一律是 <c>02 F0 80</c>），
    /// 但它算 DT 头的 3 个字节，缺失就是帧长度不符。
    /// </summary>
    /// <param name="content">TPKT 之后的字节；成功时前移过 DT 头。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>是 DT 头返回 true。</returns>
    public static bool TryTakeDataTransfer(ref ReadOnlySpan<byte> content, [NotNullWhen(false)] out string? violation)
    {
        if (!BigEndianFields.TryTakeUInt8(ref content, out byte lengthBytes) || lengthBytes != DtLengthBytes)
        {
            violation = "The response is not framed by a COTP data transfer header (length 02).";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt8(ref content, out byte pduType) || pduType != DtCode)
        {
            violation = "The response is not a COTP data transfer (PDU type F0).";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt8(ref content, out _))
        {
            violation = "The COTP data transfer is shorter than its 3-byte header.";
            return false;
        }

        violation = null;
        return true;
    }

    private static ushort RemoteTsap(int connectionType, int rack, int slot)
        => (ushort)(connectionType << ConnectionTypeShift | (rack << RackShift | slot));
}
