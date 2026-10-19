namespace Junevy.Communication.Modbus.Protocol;

/// <summary>异常码的名称与消息格式（规范 V1.1b3 第 7 节；只列出标准的九个码，P7-D10 / 第 21.5 节）。</summary>
internal static class ModbusExceptionCodes
{
    /// <summary>返回异常码的标准名称；不在标准列表中的码返回 null。</summary>
    /// <param name="code">异常码。</param>
    /// <returns>名称，或 null。</returns>
    public static string? NameOf(byte code) => code switch
    {
        0x01 => "Illegal Function",
        0x02 => "Illegal Data Address",
        0x03 => "Illegal Data Value",
        0x04 => "Server Device Failure",
        0x05 => "Acknowledge",
        0x06 => "Server Device Busy",
        0x08 => "Memory Parity Error",
        0x0A => "Gateway Path Unavailable",
        0x0B => "Gateway Target Device Failed to Respond",
        _ => null,
    };

    /// <summary>生成异常应答的错误消息，例如 <c>Modbus exception 0x02 (Illegal Data Address) for function 0x03.</c>。</summary>
    /// <param name="functionCode">请求的功能码（不含异常标志）。</param>
    /// <param name="code">异常码。</param>
    /// <returns>英文消息。</returns>
    public static string Describe(byte functionCode, byte code)
    {
        string? name = NameOf(code);
        return name is null
            ? $"Modbus exception 0x{code:X2} for function 0x{functionCode:X2}."
            : $"Modbus exception 0x{code:X2} ({name}) for function 0x{functionCode:X2}.";
    }
}
