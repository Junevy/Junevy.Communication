using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus;

/// <summary>
/// Modbus 地址文本的解析（P7-D4）。形式为 <c>[s=&lt;站号&gt;;]&lt;存储区&gt;&lt;编号&gt;[.&lt;位号&gt;]</c>，大小写不敏感。
/// 存储区有两种写法：符号写法（零基，<c>HR100</c>）与 Modicon 写法（一基，<c>40001</c>）。
/// 本解析器只检查语法与数值范围，不知道帧格式；按帧格式检查站号范围由客户端负责（RTU 1–247，MBAP 0–255）。
/// </summary>
internal static class ModbusAddressParser
{
    private const string UnitPrefix = "s=";
    private const char UnitTerminator = ';';
    private const char BitSeparator = '.';
    private const int MaxUnitId = 255;
    private const int MaxBitIndex = 15;
    private const int MaxSymbolicNumber = 65535;
    private const int ModiconFiveDigits = 5;
    private const int ModiconSixDigits = 6;
    private const int MaxModiconFiveDigitValue = 9999;
    private const int MaxModiconSixDigitValue = 65536;

    // 按符号长度降序匹配，保证最长前缀优先（与 PlcAddressParser 的约定一致）。
    private static readonly ModbusDevice[] SymbolicDevices =
    {
        ModbusDevice.DiscreteInput,
        ModbusDevice.InputRegister,
        ModbusDevice.HoldingRegister,
        ModbusDevice.Coil,
    };

    /// <summary>
    /// 解析地址文本。
    /// </summary>
    /// <param name="text">地址文本；为 null 时视为空。</param>
    /// <param name="address">解析成功时为地址；失败时为 null。</param>
    /// <param name="error">解析失败时为英文错误消息；成功时为 null。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ModbusAddress? address, [NotNullWhen(false)] out string? error)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        address = null;
        if (trimmed.Length == 0)
        {
            error = "The address is empty.";
            return false;
        }

        if (!TryReadUnitPrefix(trimmed, out byte? unitId, out int bodyStart, out error))
            return false;

        string body = trimmed.Substring(bodyStart);
        if (body.Length == 0)
        {
            error = $"The address '{trimmed}' has no device and number after the unit ID.";
            return false;
        }

        return IsAsciiDigit(body[0])
            ? TryParseModicon(trimmed, body, unitId, out address, out error)
            : TryParseSymbolic(trimmed, body, unitId, out address, out error);
    }

    // 站号前缀 s=n;（大小写不敏感）。没有前缀时 unitId 为 null，bodyStart 为 0。
    private static bool TryReadUnitPrefix(string text, out byte? unitId, out int bodyStart, [NotNullWhen(false)] out string? error)
    {
        unitId = null;
        bodyStart = 0;
        error = null;
        if (!text.StartsWith(UnitPrefix, StringComparison.OrdinalIgnoreCase))
            return true;

        int end = text.IndexOf(UnitTerminator, UnitPrefix.Length);
        if (end < 0)
        {
            error = $"The unit ID prefix in address '{text}' must end with ';', for example s=2;HR100.";
            return false;
        }

        string digits = text.Substring(UnitPrefix.Length, end - UnitPrefix.Length);
        if (!TryParseDecimal(digits, MaxUnitId, out int value))
        {
            error = $"The unit ID in address '{text}' must be a decimal number between 0 and {MaxUnitId}.";
            return false;
        }

        unitId = (byte)value;
        bodyStart = end + 1;
        return true;
    }

    // 符号写法：符号之后是 "编号[.位号]"。
    private static bool TryParseSymbolic(string text, string body, byte? unitId, out ModbusAddress? address, out string? error)
    {
        ModbusDevice? device = FindSymbol(body, out int symbolLength);
        if (device is null)
        {
            address = null;
            error = $"Unknown device in address '{text}'.";
            return false;
        }

        SplitBit(body.Substring(symbolLength), out string numberDigits, out string? bitDigits);
        if (!TryParseDecimal(numberDigits, MaxSymbolicNumber, out int number))
        {
            address = null;
            error = $"The device number in address '{text}' must be a decimal number between 0 and {MaxSymbolicNumber}.";
            return false;
        }

        return TryCreate(text, device, number, bitDigits, unitId, out address, out error);
    }

    // Modicon 写法：整个 "编号[.位号]" 以首位存储区数字开始，编号的写法由位数决定（见 TryDecodeModicon）。
    private static bool TryParseModicon(string text, string body, byte? unitId, out ModbusAddress? address, out string? error)
    {
        SplitBit(body, out string digits, out string? bitDigits);
        if (!TryDecodeModicon(digits, out ModbusDevice? device, out int number))
        {
            address = null;
            error = $"The address '{text}' is not a valid Modicon address. Use a symbolic form such as HR100, or a Modicon form such as 40001 or 400001.";
            return false;
        }

        return TryCreate(text, device, number, bitDigits, unitId, out address, out error);
    }

    // Modicon 写法：5 位（首位为存储区，后 4 位为 0001–9999）或 6 位（后 5 位为 00001–65536）。编号 = 数值 − 1。
    private static bool TryDecodeModicon(string digits, [NotNullWhen(true)] out ModbusDevice? device, out int number)
    {
        device = null;
        number = 0;
        if (digits.Length is not (ModiconFiveDigits or ModiconSixDigits))
            return false;

        device = FindModiconArea(digits[0]);
        if (device is null)
            return false;

        int maximum = digits.Length == ModiconFiveDigits ? MaxModiconFiveDigitValue : MaxModiconSixDigitValue;
        if (!TryParseDecimal(digits.Substring(1), maximum, out int value) || value < 1)
            return false;

        number = value - 1;
        return true;
    }

    // 编号之后的可选位号：只允许寄存器，取值 0–15（位 0 是寄存器值的最低位）。
    private static bool TryCreate(string text, ModbusDevice device, int number, string? bitDigits, byte? unitId,
                                  [NotNullWhen(true)] out ModbusAddress? address, [NotNullWhen(false)] out string? error)
    {
        int? bitIndex = null;
        if (bitDigits is not null)
        {
            if (device.Unit == PlcAddressUnit.Bit)
            {
                address = null;
                error = $"Bit devices do not support a bit index in address '{text}'.";
                return false;
            }

            if (!TryParseDecimal(bitDigits, MaxBitIndex, out int bit))
            {
                address = null;
                error = $"The bit index in address '{text}' must be a decimal number between 0 and {MaxBitIndex}.";
                return false;
            }

            bitIndex = bit;
        }

        address = new ModbusAddress(device, number, bitIndex, unitId, text);
        error = null;
        return true;
    }

    // "编号[.位号]" 拆成两段；没有小数点时位号为 null。
    private static void SplitBit(string body, out string numberDigits, out string? bitDigits)
    {
        int dot = body.IndexOf(BitSeparator);
        numberDigits = dot < 0 ? body : body.Substring(0, dot);
        bitDigits = dot < 0 ? null : body.Substring(dot + 1);
    }

    private static ModbusDevice? FindSymbol(string body, out int symbolLength)
    {
        foreach (ModbusDevice device in SymbolicDevices)
        {
            if (body.StartsWith(device.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                symbolLength = device.Symbol.Length;
                return device;
            }
        }

        symbolLength = 0;
        return null;
    }

    private static ModbusDevice? FindModiconArea(char area)
    {
        foreach (ModbusDevice device in SymbolicDevices)
        {
            if (device.ModiconArea == area)
                return device;
        }

        return null;
    }

    // 十进制数字（至少一位），数值不超过 maximum。饱和计算，溢出安全。
    private static bool TryParseDecimal(string digits, int maximum, out int value)
    {
        value = 0;
        if (digits.Length == 0)
            return false;

        long parsed = 0;
        foreach (char c in digits)
        {
            if (!IsAsciiDigit(c))
                return false;

            parsed = Math.Min(parsed * 10 + (c - '0'), (long)maximum + 1);
        }

        if (parsed > maximum)
            return false;

        value = (int)parsed;
        return true;
    }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';
}
