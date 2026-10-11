using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// Read Var 单项（计划第 11 节第 1、2 条）：请求按 BYTE 传输尺寸、数量按字节计；
/// 应答在返回码 0xFF 时按 17.3 节的传输尺寸换算并交出数据。
/// </summary>
internal static class S7ReadVar
{
    /// <summary>编码读请求（V-S7-5、V-S7-10）。</summary>
    /// <param name="pduReference">PDU 引用号。</param>
    /// <param name="areaCode">区域代码（17.3 节）。</param>
    /// <param name="dbNumber">DB 号；I、Q、M 区为 0。</param>
    /// <param name="byteAddress">起始字节号。</param>
    /// <param name="byteCount">读取的字节数。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildRequest(ushort pduReference, byte areaCode, int dbNumber, int byteAddress, int byteCount)
    {
        using var parameters = new ByteBufferWriter(S7VarItem.ParameterBytes);
        S7VarItem.WriteParameters(
            parameters, S7VarItem.ReadService, S7VarItem.RequestByte, byteCount, areaCode, dbNumber, byteAddress, 0);

        return S7Message.BuildRequest(pduReference, parameters.WrittenSpan, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// 解码读应答（V-S7-6、V-S7-10 的应答）：应答头错误优先，其次数据项返回码（P4-D14），
    /// 然后校验数据项长度与 <paramref name="byteCount"/> 一致（计划第 11 节第 2 条）。
    /// </summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="expectedReference">请求使用的 PDU 引用号。</param>
    /// <param name="byteCount">请求的字节数。</param>
    /// <returns>读到的字节；帧结构错误为 <see cref="CommErrorKind.ProtocolViolation"/>，设备侧错误为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult<byte[]> ReadResponse(ReadOnlySpan<byte> frame, ushort expectedReference, int byteCount)
    {
        if (!S7Message.TryOpenResponse(frame, expectedReference, out byte errorClass, out byte errorCode,
                out ReadOnlySpan<byte> parameters, out ReadOnlySpan<byte> data, out string? violation))
            return S7Errors.Violation<byte[]>(violation);

        CommResult header = S7Errors.CheckHeader(errorClass, errorCode);
        if (!header.IsSuccess)
            return header.As<byte[]>();

        if (!S7VarItem.TryTakeResponseParameters(parameters, S7VarItem.ReadService, out violation))
            return S7Errors.Violation<byte[]>(violation);

        if (!S7VarItem.TryTakeItemHeader(ref data, out byte returnCode, out byte transportSize, out int length, out violation))
            return S7Errors.Violation<byte[]>(violation);

        // 返回码非 0xFF 时数据不可信，长度字段也不再解释（V-S7-11）。
        if (returnCode != S7VarItem.Success)
            return S7Errors.ItemFailure<byte[]>(returnCode);

        if (!S7VarItem.TryCountPayloadBytes(transportSize, length, out int payloadBytes))
            return S7Errors.Violation<byte[]>(
                $"The read item uses transport size 0x{transportSize:X2}, which is not in the code table (17.3).");

        if (payloadBytes != byteCount)
            return S7Errors.Violation<byte[]>(
                $"The read item carries {payloadBytes} byte(s); the request asked for {byteCount}.");

        if (!S7VarItem.TryTakePayload(ref data, payloadBytes, out ReadOnlySpan<byte> payload, out violation))
            return S7Errors.Violation<byte[]>(violation);

        return CommResult<byte[]>.Success(payload.ToArray());
    }
}
