using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Omron.Protocol;

/// <summary>
/// 内存区读写（计划 Task 12 第 4、5 条）：命令 0101 读、0102 写，参数一律是"区域 1 + 字地址 2 + 位号 1 + 数量 2"
/// （第 17.2 节说明段），字与位共用这一段参数写入代码，只有区域代码与数量单位不同（第 18 节自查项）。
/// 位数据每点 1 字节，取值 0x00 或 0x01。应答解码依次检查外壳、命令、ICF 应答位、SID 与命令码回显、结束码（P4-D14），
/// 最后比对数据长度。
/// </summary>
internal static class FinsMemoryArea
{
    /// <summary>读服务码 0101（V-F-3、V-F-6）。</summary>
    public const ushort CommandRead = 0x0101;

    /// <summary>写服务码 0102（V-F-5、V-F-7）。</summary>
    public const ushort CommandWrite = 0x0102;

    /// <summary>参数块字节数：区域 1 + 字地址 2 + 位号 1 + 数量 2。</summary>
    public const int ParametersBytes = 6;

    /// <summary>一个字 2 字节；字访问的数据长度与数量都按它换算。</summary>
    public const int WordBytes = 2;

    /// <summary>位数据每点 1 字节。</summary>
    public const int BytesPerBitPoint = 1;

    // 命令码在参数之前，应答里是它的回显。
    private const int CommandCodeBytes = 2;

    private const byte BitOn = 0x01;
    private const byte BitOff = 0x00;

    /// <summary>编码内存区读请求（V-F-3 用字区代码、位号 0；V-F-6 用位区代码、每点 1 字节的数量）。</summary>
    /// <param name="route">路由字节。</param>
    /// <param name="sessionId">SID（P4-D17）。</param>
    /// <param name="areaCode">区域代码（第 17.3 节）。</param>
    /// <param name="wordAddress">起始字号。</param>
    /// <param name="bitNumber">起始位号；字访问为 0。</param>
    /// <param name="count">数量，字访问按字计、位访问按点计。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildReadRequest(
        in FinsRoute route, byte sessionId, byte areaCode, int wordAddress, int bitNumber, int count)
        => BuildRequest(route, sessionId, CommandRead, areaCode, wordAddress, bitNumber, count, ReadOnlySpan<byte>.Empty);

    /// <summary>编码字写请求（V-F-5）：数量是 <paramref name="words"/> 的字数。</summary>
    /// <param name="route">路由字节。</param>
    /// <param name="sessionId">SID。</param>
    /// <param name="areaCode">字区代码（第 17.3 节）。</param>
    /// <param name="wordAddress">起始字号。</param>
    /// <param name="words">要写入的字节，每字 2 字节（规范化布局）。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildWriteRequest(
        in FinsRoute route, byte sessionId, byte areaCode, int wordAddress, ReadOnlySpan<byte> words)
        => BuildRequest(route, sessionId, CommandWrite, areaCode, wordAddress, 0, words.Length / WordBytes, words);

    /// <summary>编码位写请求（V-F-7）：数量是点数，每点写 1 字节的 0x00 或 0x01，跨字时由调用方连续排布。</summary>
    /// <param name="route">路由字节。</param>
    /// <param name="sessionId">SID。</param>
    /// <param name="bitAreaCode">位区代码（第 17.3 节）。</param>
    /// <param name="wordAddress">起始字号。</param>
    /// <param name="bitNumber">起始位号（0–15）。</param>
    /// <param name="values">要写入的点值。</param>
    /// <returns>完整的请求帧。</returns>
    public static byte[] BuildWriteBitsRequest(
        in FinsRoute route, byte sessionId, byte bitAreaCode, int wordAddress, int bitNumber, ReadOnlySpan<bool> values)
    {
        byte[] data = EncodeBits(values);
        return BuildRequest(route, sessionId, CommandWrite, bitAreaCode, wordAddress, bitNumber, values.Length, data);
    }

    /// <summary>解码读应答（V-F-4、V-F-6 的应答）：数据长度必须与请求一致。</summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="expectedSessionId">请求使用的 SID。</param>
    /// <param name="payloadBytes">请求的数据字节数：字访问每字 2 字节、位访问每点 1 字节。</param>
    /// <param name="logger">记录非致命结束码的日志器；不需要日志时为 null。</param>
    /// <returns>读到的字节；结构错误为 <see cref="CommErrorKind.ProtocolViolation"/>，设备侧错误为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult<byte[]> ReadResponse(ReadOnlySpan<byte> frame, byte expectedSessionId, int payloadBytes, ILogger? logger)
    {
        CommResult<byte[]> opened = OpenResponse(frame, expectedSessionId, CommandRead, logger);
        if (!opened.IsSuccess)
            return opened;

        int returned = opened.Data!.Length;
        return returned == payloadBytes
            ? opened
            : FinsErrors.Violation<byte[]>(
                $"The FINS read response carries {returned} data byte(s); the request asked for {payloadBytes}.");
    }

    /// <summary>解码写应答（V-F-5 的应答）：结束码之后不应有数据。</summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="expectedSessionId">请求使用的 SID。</param>
    /// <param name="logger">记录非致命结束码的日志器；不需要日志时为 null。</param>
    /// <returns>结构错误为 <see cref="CommErrorKind.ProtocolViolation"/>，设备侧错误为 <see cref="CommErrorKind.RemoteError"/>。</returns>
    public static CommResult CheckWriteResponse(ReadOnlySpan<byte> frame, byte expectedSessionId, ILogger? logger)
    {
        CommResult<byte[]> opened = OpenResponse(frame, expectedSessionId, CommandWrite, logger);
        if (!opened.IsSuccess)
            return opened.ToResult();

        return opened.Data!.Length == 0
            ? CommResult.Success()
            : FinsErrors.Violation($"The FINS write acknowledgement carries {opened.Data.Length} data byte(s); it has none.");
    }

    // Task 12 第 5 条：命令必须是 2，ICF 的应答位必须置位，SID 与命令码回显必须一致，结束码按 P4-D14 屏蔽。
    // P4-D14 的头错误码排在结构判断之前：出错时 PLC 可以不带完整的 FINS 帧。
    private static CommResult<byte[]> OpenResponse(ReadOnlySpan<byte> frame, byte expectedSessionId, ushort expectedCommand, ILogger? logger)
    {
        if (!FinsTcpFrame.TryUnwrap(frame, out uint command, out uint error, out ReadOnlySpan<byte> body, out string? violation))
            return FinsErrors.Violation<byte[]>(violation);

        CommResult tcp = FinsErrors.CheckTcpError(error);
        if (!tcp.IsSuccess)
            return tcp.As<byte[]>();

        if (command != FinsTcpFrame.CommandFinsFrame)
            return FinsErrors.Violation<byte[]>(
                $"The response carries FINS/TCP command {command}; expected {FinsTcpFrame.CommandFinsFrame} (a FINS frame).");

        if (!FinsHead.TryTakeResponse(ref body, expectedSessionId, out violation))
            return FinsErrors.Violation<byte[]>(violation);

        if (!FinsFields.TryTakeUInt16(ref body, out ushort echoed) || echoed != expectedCommand)
            return FinsErrors.Violation<byte[]>(
                $"The response echoes command 0x{echoed:X4}; the request used 0x{expectedCommand:X4}.");

        if (!FinsFields.TryTakeUInt8(ref body, out byte major) || !FinsFields.TryTakeUInt8(ref body, out byte minor))
            return FinsErrors.Violation<byte[]>("The FINS response carries no end code.");

        CommResult endCode = FinsErrors.CheckEndCode(major, minor, logger);
        if (!endCode.IsSuccess)
            return endCode.As<byte[]>();

        return CommResult<byte[]>.Success(body.ToArray());
    }

    private static byte[] BuildRequest(
        in FinsRoute route,
        byte sessionId,
        ushort command,
        byte areaCode,
        int wordAddress,
        int bitNumber,
        int count,
        ReadOnlySpan<byte> data)
    {
        int bodyBytes = FinsHead.HeadBytes + CommandCodeBytes + ParametersBytes + data.Length;
        using var buffer = new ByteBufferWriter(FinsTcpFrame.ShellBytes + bodyBytes);
        FinsTcpFrame.WriteShell(buffer, FinsTcpFrame.CommandFinsFrame, error: 0, bodyBytes);
        FinsHead.WriteRequest(buffer, route, sessionId);
        FinsFields.WriteUInt16(buffer, command);
        WriteParameters(buffer, areaCode, wordAddress, bitNumber, count);
        FinsFields.WriteRaw(buffer, data);
        return buffer.ToArray();
    }

    // 字与位共用的参数写入（第 18 节自查项）：数量单位由调用方决定，字地址与位号都是 2 字节与 1 字节字段。
    private static void WriteParameters(IBufferWriter<byte> buffer, byte areaCode, int wordAddress, int bitNumber, int count)
    {
        FinsFields.WriteUInt8(buffer, areaCode);
        FinsFields.WriteUInt16(buffer, checked((ushort)wordAddress));
        FinsFields.WriteUInt8(buffer, checked((byte)bitNumber));
        FinsFields.WriteUInt16(buffer, checked((ushort)count));
    }

    private static byte[] EncodeBits(ReadOnlySpan<bool> values)
    {
        using var buffer = new ByteBufferWriter(values.Length * BytesPerBitPoint);
        foreach (bool value in values)
        {
            FinsFields.WriteUInt8(buffer, value ? BitOn : BitOff);
        }

        return buffer.ToArray();
    }
}
