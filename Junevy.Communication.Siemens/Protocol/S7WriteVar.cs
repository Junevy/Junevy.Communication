using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// Write Var 单项（计划第 11 节第 1、2 条）：字节写的地址项按 BYTE 计数量、数据项传输尺寸为 0x04 且长度按位计；
/// 位写的地址项与数据项都用 BIT，数量与长度都是 1。应答只有一个返回码。
/// </summary>
internal static class S7WriteVar
{
    /// <summary>位写只有一个位：地址项的数量与数据项的长度都是 1（计划第 11 节第 1 条）。</summary>
    private const int SingleBit = 1;

    private const byte BitOn = 0x01;
    private const byte BitOff = 0x00;

    /// <summary>编码字节写请求（V-S7-7）。</summary>
    /// <param name="pduReference">PDU 引用号。</param>
    /// <param name="areaCode">区域代码（17.3 节）。</param>
    /// <param name="dbNumber">DB 号；I、Q、M 区为 0。</param>
    /// <param name="byteAddress">起始字节号。</param>
    /// <param name="value">要写入的字节。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildRequest(
        ushort pduReference, byte areaCode, int dbNumber, int byteAddress, ReadOnlySpan<byte> value)
    {
        using var parameters = new ByteBufferWriter(S7VarItem.ParameterBytes);
        S7VarItem.WriteParameters(
            parameters, S7VarItem.WriteService, S7VarItem.RequestByte, value.Length, areaCode, dbNumber, byteAddress, 0);

        using var data = new ByteBufferWriter(S7VarItem.ItemHeaderBytes + value.Length);
        S7VarItem.WriteDataUnitHeader(data, S7VarItem.DataByte, value.Length * S7VarItem.BitsPerByte);
        S7Fields.WriteRaw(data, value);

        return S7Message.BuildRequest(pduReference, parameters.WrittenSpan, data.WrittenSpan);
    }

    /// <summary>编码位写请求（V-S7-9）：地址项的数量是 1 个位，数据项传输尺寸 0x03、长度 1，值占 1 字节。</summary>
    /// <param name="pduReference">PDU 引用号。</param>
    /// <param name="areaCode">区域代码（17.3 节）。</param>
    /// <param name="dbNumber">DB 号；I、Q、M 区为 0。</param>
    /// <param name="byteAddress">字节号。</param>
    /// <param name="bitIndex">位号（0–7）。</param>
    /// <param name="value">写入的位值。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildBitRequest(
        ushort pduReference, byte areaCode, int dbNumber, int byteAddress, int bitIndex, bool value)
    {
        using var parameters = new ByteBufferWriter(S7VarItem.ParameterBytes);
        S7VarItem.WriteParameters(
            parameters, S7VarItem.WriteService, S7VarItem.RequestBit, SingleBit, areaCode, dbNumber, byteAddress, bitIndex);

        using var data = new ByteBufferWriter(S7VarItem.ItemHeaderBytes + S7Fields.UInt8Bytes);
        S7VarItem.WriteDataUnitHeader(data, S7VarItem.DataBit, SingleBit);
        S7Fields.WriteUInt8(data, value ? BitOn : BitOff);

        return S7Message.BuildRequest(pduReference, parameters.WrittenSpan, data.WrittenSpan);
    }

    /// <summary>解码写应答（V-S7-8）：只有返回码，其后的填充字节不参与判断。</summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="expectedReference">请求使用的 PDU 引用号。</param>
    /// <returns>帧结构错误为 <see cref="CommErrorKind.ProtocolViolation"/>，设备侧错误为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult ReadResponse(ReadOnlySpan<byte> frame, ushort expectedReference)
    {
        if (!S7Message.TryOpenResponse(frame, expectedReference, out byte errorClass, out byte errorCode,
                out ReadOnlySpan<byte> parameters, out ReadOnlySpan<byte> data, out string? violation))
            return S7Errors.Violation(violation);

        CommResult header = S7Errors.CheckHeader(errorClass, errorCode);
        if (!header.IsSuccess)
            return header;

        if (!S7VarItem.TryTakeResponseParameters(parameters, S7VarItem.WriteService, out violation))
            return S7Errors.Violation(violation);

        if (!S7Fields.TryTakeUInt8(ref data, out byte returnCode))
            return S7Errors.Violation("The write acknowledgement carries no item return code.");

        return returnCode == S7VarItem.Success
            ? CommResult.Success()
            : S7Errors.ItemFailure(returnCode);
    }
}
