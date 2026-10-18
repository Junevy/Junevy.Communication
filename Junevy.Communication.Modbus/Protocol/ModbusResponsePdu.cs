using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Modbus.Protocol;

/// <summary>
/// PDU 应答的解码（第 21.1 节）：正常应答、异常应答与回显校验。按期望（<see cref="ModbusExpectation"/>）校验功能码、字节数与回显，
/// 失败时返回 <c>RemoteError</c>（异常应答）或 <c>ProtocolViolation</c>（格式不符）。站号与帧的校验由帧层完成。
/// </summary>
internal static class ModbusResponsePdu
{
    /// <summary>解码位读取（01、02）的应答，返回 <see cref="ModbusExpectation.Count"/> 个点。</summary>
    public static CommResult<bool[]> DecodeBits(ReadOnlySpan<byte> pdu, ModbusExpectation expectation)
    {
        CommResult? failure = CheckHeader(pdu, expectation.FunctionCode) ?? CheckDataLength(pdu, expectation);
        if (failure is not null)
            return failure.As<bool[]>();

        return CommResult<bool[]>.Success(ModbusBits.Unpack(pdu.Slice(2), expectation.Count));
    }

    /// <summary>解码字读取（03、04、17）的应答，返回每寄存器两字节的大端数据。</summary>
    public static CommResult<byte[]> DecodeWords(ReadOnlySpan<byte> pdu, ModbusExpectation expectation)
    {
        CommResult? failure = CheckHeader(pdu, expectation.FunctionCode) ?? CheckDataLength(pdu, expectation);
        if (failure is not null)
            return failure.As<byte[]>();

        return CommResult<byte[]>.Success(pdu.Slice(2).ToArray());
    }

    /// <summary>解码写入（05、06、0F、10、16）的应答：必须原样回显请求的字节。</summary>
    public static CommResult DecodeEcho(ReadOnlySpan<byte> pdu, ModbusExpectation expectation)
    {
        CommResult? failure = CheckHeader(pdu, expectation.FunctionCode);
        if (failure is not null)
            return failure;

        byte[] echo = expectation.Echo ?? throw new InvalidOperationException("A write expectation must carry the bytes to echo.");
        if (pdu.Length != 1 + echo.Length)
        {
            return CommResult.Fail(
                $"The echoed response length does not match the request: {pdu.Length} byte(s), expected {1 + echo.Length}.",
                CommErrorKind.ProtocolViolation);
        }

        if (!pdu.Slice(1).SequenceEqual(echo))
        {
            return CommResult.Fail($"The echoed value does not match the request for function 0x{expectation.FunctionCode:X2}.",
                                   CommErrorKind.ProtocolViolation);
        }

        return CommResult.Success();
    }

    // 异常应答与功能码不符在任何数据解码之前处理。异常应答的功能码是请求功能码与 0x80 的或，长度必须恰为两字节。
    private static CommResult? CheckHeader(ReadOnlySpan<byte> pdu, byte functionCode)
    {
        if (pdu.Length == 0)
            return CommResult.Fail("The response PDU is empty.", CommErrorKind.ProtocolViolation);

        byte received = pdu[0];
        if (received == (functionCode | ModbusFunctionCodes.ExceptionFlag))
        {
            if (pdu.Length != 2)
                return CommResult.Fail($"The exception response for function 0x{functionCode:X2} must be 2 bytes long.", CommErrorKind.ProtocolViolation);

            byte code = pdu[1];
            return CommResult.Fail(ModbusExceptionCodes.Describe(functionCode, code), CommErrorKind.RemoteError, code);
        }

        return received == functionCode
            ? null
            : CommResult.Fail($"The response function code 0x{received:X2} does not match the request 0x{functionCode:X2}.", CommErrorKind.ProtocolViolation);
    }

    // 读取应答：第二个字节是数据字节数，必须等于期望值，并且整个 PDU 只包含这些数据。
    private static CommResult? CheckDataLength(ReadOnlySpan<byte> pdu, ModbusExpectation expectation)
    {
        if (pdu.Length < 2)
            return CommResult.Fail("The response is too short to carry a byte count.", CommErrorKind.ProtocolViolation);

        int byteCount = pdu[1];
        int expected = expectation.ExpectedDataByteCount;
        if (byteCount != expected || pdu.Length != 2 + byteCount)
            return CommResult.Fail($"The response carries {byteCount} data byte(s), expected {expected}.", CommErrorKind.ProtocolViolation);

        return null;
    }
}
