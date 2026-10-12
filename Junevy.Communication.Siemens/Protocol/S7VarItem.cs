using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// Read/Write Var 的单项格式（计划第 17.1 节说明段与第 17.3 节码表）：参数块里的地址项、数据块里的数据项头，
/// 以及应答侧对这两段的切取与长度换算。本期一律单 item 请求（P4-D12）。
/// </summary>
internal static class S7VarItem
{
    /// <summary>读服务号 0x04。</summary>
    public const byte ReadService = 0x04;

    /// <summary>写服务号 0x05。</summary>
    public const byte WriteService = 0x05;

    /// <summary>数据项返回码 0xFF：成功（17.3 节）。</summary>
    public const byte Success = 0xFF;

    /// <summary>地址项传输尺寸 0x01：BIT（17.3 节，数量按位计）。</summary>
    public const byte RequestBit = 0x01;

    /// <summary>地址项传输尺寸 0x02：BYTE（17.3 节，数量按字节计）。</summary>
    public const byte RequestByte = 0x02;

    /// <summary>数据项传输尺寸 0x03：BIT，长度按位计（17.3 节）。</summary>
    public const byte DataBit = 0x03;

    /// <summary>数据项传输尺寸 0x04：BYTE/WORD/DWORD，长度按位计（17.3 节）。</summary>
    public const byte DataByte = 0x04;

    /// <summary>数据项传输尺寸 0x09：OCTET STRING，长度按字节计（17.3 节）。</summary>
    public const byte DataOctetString = 0x09;

    /// <summary>一个字节包含的位数，用于把按位计的长度换算成字节数。</summary>
    public const int BitsPerByte = 8;

    /// <summary>参数块中单项地址项的字节数：服务号、项数与 12 字节的地址项。</summary>
    public const int ParameterBytes = 14;

    /// <summary>数据块中数据项头的字节数：返回码、传输尺寸与 2 字节长度。</summary>
    public const int ItemHeaderBytes = 4;

    private const byte SingleItem = 0x01;

    // 地址项的三个引导字节（17.1 节说明段：`12 0A 10` + 传输尺寸 + 数量 + DB 号 + 区域 + 地址）。
    private const byte SpecSignature = 0x12;
    private const byte SpecLength = 0x0A;
    private const byte SpecSyntaxId = 0x10;

    // 数据项头之前的填充字节（V-S7-7、V-S7-9 中的 0x00）。
    private const byte DataUnitFiller = 0x00;

    /// <summary>
    /// 写单项读或写的参数块。地址字段是 3 字节，值为字节号 × 8 + 位号（第 17.1 节说明段）。
    /// </summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="service">服务号：读 0x04、写 0x05。</param>
    /// <param name="requestTransportSize">地址项传输尺寸（17.3 节的 0x01 或 0x02）。</param>
    /// <param name="quantity">数量，按 <paramref name="requestTransportSize"/> 的单位计。</param>
    /// <param name="areaCode">区域代码（17.3 节）。</param>
    /// <param name="dbNumber">DB 号；I、Q、M 区为 0。</param>
    /// <param name="byteAddress">字节号。</param>
    /// <param name="bitIndex">位号；字节访问为 0。</param>
    public static void WriteParameters(
        IBufferWriter<byte> buffer,
        byte service,
        byte requestTransportSize,
        int quantity,
        byte areaCode,
        int dbNumber,
        int byteAddress,
        int bitIndex)
    {
        BigEndianFields.WriteUInt8(buffer, service);
        BigEndianFields.WriteUInt8(buffer, SingleItem);
        BigEndianFields.WriteUInt8(buffer, SpecSignature);
        BigEndianFields.WriteUInt8(buffer, SpecLength);
        BigEndianFields.WriteUInt8(buffer, SpecSyntaxId);
        BigEndianFields.WriteUInt8(buffer, requestTransportSize);
        BigEndianFields.WriteUInt16(buffer, checked((ushort)quantity));
        BigEndianFields.WriteUInt16(buffer, checked((ushort)dbNumber));
        BigEndianFields.WriteUInt8(buffer, areaCode);

        // 地址字段的上限由 S7Client.Hooks.ValidateRange 保证：字节号不超过 S7Device.MaxNumber（0xFFFFFF / 8），
        // 位号不超过 7，因此字节号 × 8 + 位号 最大正好是 0xFFFFFF，落在 UInt24 的取值范围内。
        BigEndianFields.WriteUInt24(buffer, byteAddress * BitsPerByte + bitIndex);
    }

    /// <summary>写数据项头：<c>00</c> 填充、传输尺寸与 2 字节长度（长度单位由 <paramref name="transportSize"/> 决定，见 17.3 节）。</summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="transportSize">数据项传输尺寸。</param>
    /// <param name="length">长度数值。</param>
    public static void WriteDataUnitHeader(IBufferWriter<byte> buffer, byte transportSize, int length)
    {
        BigEndianFields.WriteUInt8(buffer, DataUnitFiller);
        BigEndianFields.WriteUInt8(buffer, transportSize);
        BigEndianFields.WriteUInt16(buffer, checked((ushort)length));
    }

    /// <summary>
    /// 校验应答参数块（计划第 11 节第 2 条）：服务号与期望一致，且只有一项——本期不做多项请求（P4-D12）。
    /// </summary>
    /// <param name="parameters">应答参数块。</param>
    /// <param name="expectedService">期望的服务号。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>结构正确返回 true。</returns>
    public static bool TryTakeResponseParameters(
        ReadOnlySpan<byte> parameters, byte expectedService, [NotNullWhen(false)] out string? violation)
    {
        ReadOnlySpan<byte> rest = parameters;
        if (!BigEndianFields.TryTakeUInt8(ref rest, out byte service) || service != expectedService)
        {
            violation = $"The response parameters carry service 0x{service:X2}; expected 0x{expectedService:X2}.";
            return false;
        }

        if (!BigEndianFields.TryTakeUInt8(ref rest, out byte itemCount) || itemCount != SingleItem)
        {
            violation = $"The response carries {itemCount} item(s); this release sends one item per request (P4-D12).";
            return false;
        }

        if (rest.Length != 0)
        {
            violation = $"The response parameters carry {rest.Length} unexpected byte(s) after the item count.";
            return false;
        }

        violation = null;
        return true;
    }

    /// <summary>取出数据项头的三个字段；返回码是否算失败由调用方判断（P4-D14）。</summary>
    /// <param name="data">数据块；成功时前移过数据项头。</param>
    /// <param name="returnCode">数据项返回码。</param>
    /// <param name="transportSize">数据项传输尺寸。</param>
    /// <param name="length">长度数值，单位由 <paramref name="transportSize"/> 决定。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>字节足够返回 true。</returns>
    public static bool TryTakeItemHeader(
        ref ReadOnlySpan<byte> data,
        out byte returnCode,
        out byte transportSize,
        out int length,
        [NotNullWhen(false)] out string? violation)
    {
        returnCode = 0;
        transportSize = 0;
        length = 0;

        ReadOnlySpan<byte> rest = data;
        if (!BigEndianFields.TryTakeUInt8(ref rest, out returnCode)
            || !BigEndianFields.TryTakeUInt8(ref rest, out transportSize)
            || !BigEndianFields.TryTakeUInt16(ref rest, out ushort unitLength))
        {
            violation = "The response data carries no complete item header (return code, transport size, length).";
            return false;
        }

        violation = null;
        length = unitLength;
        data = rest;
        return true;
    }

    /// <summary>把数据项长度换算成字节数（17.3 节）：0x09 按字节计，0x03 与 0x04 按位计并向上取整。</summary>
    /// <param name="transportSize">数据项传输尺寸。</param>
    /// <param name="length">长度数值。</param>
    /// <param name="payloadBytes">字节数；尺寸不在 17.3 节的表内时为 0。</param>
    /// <returns>尺寸在表内返回 true。</returns>
    public static bool TryCountPayloadBytes(byte transportSize, int length, out int payloadBytes)
    {
        switch (transportSize)
        {
            case DataOctetString:
                payloadBytes = length;
                return true;

            case DataBit:
            case DataByte:
                // 长度不足一个字节时向上取整（例如 3 位承载在 1 字节里）。
                payloadBytes = (length + BitsPerByte - 1) / BitsPerByte;
                return true;

            // 表外的尺寸（例如 0x05）不猜测单位：宁可判为帧结构错误。
            default:
                payloadBytes = 0;
                return false;
        }
    }

    /// <summary>切出数据项负载，并要求数据块在负载之后没有多余字节（17.1 节说明段：单项不补填充）。</summary>
    /// <param name="data">数据项头之后的字节；成功时指向负载。</param>
    /// <param name="payloadBytes">负载字节数。</param>
    /// <param name="payload">负载；结构不符时为空。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>长度正好返回 true。</returns>
    public static bool TryTakePayload(
        ref ReadOnlySpan<byte> data,
        int payloadBytes,
        out ReadOnlySpan<byte> payload,
        [NotNullWhen(false)] out string? violation)
    {
        payload = ReadOnlySpan<byte>.Empty;

        if (!BigEndianFields.TryTakeBytes(ref data, payloadBytes, out ReadOnlySpan<byte> taken))
        {
            violation = $"The response announces {payloadBytes} data byte(s) but carries {data.Length}.";
            return false;
        }

        if (data.Length != 0)
        {
            violation = $"The response data carries {data.Length} byte(s) after the announced item.";
            return false;
        }

        violation = null;
        payload = taken;
        return true;
    }
}
