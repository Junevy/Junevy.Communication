using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Omron.Protocol;

/// <summary>
/// FINS/TCP 外壳（计划第 17.2 节说明段）：魔数 "FINS" 4 字节 + 长度 4 字节（大端，计长度字段之后的全部字节）
/// + 命令 4 字节 + 错误码 4 字节。解包只判断帧结构（截断、长度不符、魔数错误，Task 12 第 6 条）；
/// 命令取值由调用方判断，错误码非 0 按 P4-D14 映射为设备侧错误。
/// </summary>
internal static class FinsTcpFrame
{
    /// <summary>外壳字节数：魔数 4 + 长度 4 + 命令 4 + 错误码 4。</summary>
    public const int ShellBytes = 16;

    /// <summary>命令 0：节点地址请求（V-F-1）。</summary>
    public const uint CommandNodeRequest = 0;

    /// <summary>命令 1：节点地址应答（V-F-2）。</summary>
    public const uint CommandNodeResponse = 1;

    /// <summary>命令 2：FINS 帧（V-F-3 起的每一条读写请求与其应答）。</summary>
    public const uint CommandFinsFrame = 2;

    // 长度字段的值不含魔数与自身，所以声明值等于 8 加上命令、错误码与体。
    private const int MagicAndLengthBytes = 8;

    private const byte MagicF = 0x46;
    private const byte MagicI = 0x49;
    private const byte MagicN = 0x4E;
    private const byte MagicS = 0x53;

    /// <summary>把体套进外壳，帧长与长度字段按同一次计算写入。</summary>
    /// <param name="command">命令取值。</param>
    /// <param name="error">FINS/TCP 错误码；请求为 0。</param>
    /// <param name="body">外壳之后的字节。</param>
    /// <returns>完整的帧。</returns>
    public static byte[] Build(uint command, uint error, ReadOnlySpan<byte> body)
    {
        using var buffer = new ByteBufferWriter(ShellBytes + body.Length);
        WriteShell(buffer, command, error, body.Length);
        FinsFields.WriteRaw(buffer, body);
        return buffer.ToArray();
    }

    /// <summary>
    /// 向已有缓冲写入外壳。体的长度必须先知道（分帧器按它读取应答），所以调用方在自己的字节写出之前先调这里。
    /// </summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="command">命令取值。</param>
    /// <param name="error">FINS/TCP 错误码；请求为 0。</param>
    /// <param name="bodyBytes">外壳之后将要写入的体长度。</param>
    public static void WriteShell(IBufferWriter<byte> buffer, uint command, uint error, int bodyBytes)
    {
        FinsFields.WriteUInt8(buffer, MagicF);
        FinsFields.WriteUInt8(buffer, MagicI);
        FinsFields.WriteUInt8(buffer, MagicN);
        FinsFields.WriteUInt8(buffer, MagicS);
        FinsFields.WriteUInt32(buffer, checked((uint)(MagicAndLengthBytes + bodyBytes)));
        FinsFields.WriteUInt32(buffer, command);
        FinsFields.WriteUInt32(buffer, error);
    }

    /// <summary>校验外壳并切出体：魔数、壳长与长度字段都要对得上。</summary>
    /// <param name="frame">完整的帧。</param>
    /// <param name="command">命令取值；结构不符时为 0。</param>
    /// <param name="error">FINS/TCP 错误码；结构不符时为 0。</param>
    /// <param name="body">外壳之后的字节；结构不符时为空。</param>
    /// <param name="violation">结构错误描述；成功时为 null。</param>
    /// <returns>帧结构正确返回 true。</returns>
    public static bool TryUnwrap(
        ReadOnlySpan<byte> frame,
        out uint command,
        out uint error,
        out ReadOnlySpan<byte> body,
        [NotNullWhen(false)] out string? violation)
    {
        command = 0;
        error = 0;
        body = ReadOnlySpan<byte>.Empty;

        if (frame.Length < ShellBytes)
        {
            violation = $"The FINS/TCP frame is {frame.Length} byte(s); the shell alone needs {ShellBytes}.";
            return false;
        }

        ReadOnlySpan<byte> rest = frame;
        if (!FinsFields.TryTakeUInt8(ref rest, out byte magic0) || magic0 != MagicF
            || !FinsFields.TryTakeUInt8(ref rest, out byte magic1) || magic1 != MagicI
            || !FinsFields.TryTakeUInt8(ref rest, out byte magic2) || magic2 != MagicN
            || !FinsFields.TryTakeUInt8(ref rest, out byte magic3) || magic3 != MagicS)
        {
            violation = "The frame does not start with the FINS/TCP magic 'FINS'.";
            return false;
        }

        if (!FinsFields.TryTakeUInt32(ref rest, out uint declaredBytes))
        {
            violation = "The FINS/TCP shell carries no length field.";
            return false;
        }

        if (declaredBytes != checked((uint)(frame.Length - MagicAndLengthBytes)))
        {
            violation = $"The FINS/TCP length {declaredBytes} does not match the {frame.Length}-byte frame.";
            return false;
        }

        if (!FinsFields.TryTakeUInt32(ref rest, out command))
        {
            violation = "The FINS/TCP shell carries no command.";
            return false;
        }

        if (!FinsFields.TryTakeUInt32(ref rest, out error))
        {
            violation = "The FINS/TCP shell carries no error code.";
            return false;
        }

        violation = null;
        body = rest;
        return true;
    }
}
