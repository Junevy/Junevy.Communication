using System.Diagnostics.CodeAnalysis;

namespace Junevy.Communication.Plc;

/// <summary>
/// 声明式地址解析器：由软元件表解析 "D100"、"x1a"、"ZR1F"、"D100.5" 这类文本。
/// 规则：去掉首尾空白；符号大小写不敏感，取最长前缀且匹配后不回退到更短的前缀；编号按软元件的进制解析并检查上限；
/// 位软元件不能带位号，字软元件的位号为 0–15，字节软元件为 0–7（进制由构造参数决定）；失败消息为英文，并包含原始文本。
/// </summary>
/// <typeparam name="TDevice">软元件定义的类型。</typeparam>
/// <typeparam name="TAddress">解析结果的地址类型。</typeparam>
public sealed class PlcAddressParser<TDevice, TAddress>
    where TDevice : IPlcDeviceDefinition
    where TAddress : PlcAddress
{
    private const int DecimalRadix = 10;
    private const int HexadecimalRadix = 16;
    private const int FirstHexLetterValue = 10;
    private const int BitsPerWord = 16;
    private const int BitsPerByte = 8;
    private const int MaxWordBitIndex = BitsPerWord - 1;
    private const int MaxByteBitIndex = BitsPerByte - 1;

    private readonly TDevice[] devicesByPrefixLength;
    private readonly Func<TDevice, int, int?, string, TAddress> create;
    private readonly int bitIndexRadix;

    /// <summary>
    /// 创建解析器。软元件表在构造时整体校验。
    /// </summary>
    /// <param name="devices">软元件表：符号不能为空，且忽略大小写后唯一；进制必须是 10 或 16；编号上限不能为负。</param>
    /// <param name="create">解析成功后创建地址的工厂。参数依次为软元件、编号、位号（没有时为 null）与去掉首尾空白的原始文本。</param>
    /// <param name="bitIndexRadix">位号的进制，10 或 16。默认 10；MELSEC 使用 16（例如 <c>D100.F</c>）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices"/> 或 <paramref name="create"/> 为 null。</exception>
    /// <exception cref="ArgumentException">软元件表含有 null、空符号，或符号重复。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitIndexRadix"/> 不是 10 或 16，或某个软元件的进制或编号上限不合法。</exception>
    public PlcAddressParser(IEnumerable<TDevice> devices, Func<TDevice, int, int?, string, TAddress> create, int bitIndexRadix = DecimalRadix)
    {
        if (devices is null)
            throw new ArgumentNullException(nameof(devices));
        if (create is null)
            throw new ArgumentNullException(nameof(create));
        if (bitIndexRadix is not (DecimalRadix or HexadecimalRadix))
            throw new ArgumentOutOfRangeException(nameof(bitIndexRadix), bitIndexRadix, "The bit index radix must be 10 or 16.");

        devicesByPrefixLength = SortByPrefixLength(devices);
        this.create = create;
        this.bitIndexRadix = bitIndexRadix;
    }

    /// <summary>
    /// 解析地址文本。
    /// </summary>
    /// <param name="text">地址文本；为 null 时视为空。</param>
    /// <param name="address">解析成功时为地址；失败时为 null。</param>
    /// <param name="error">解析失败时为英文错误消息；成功时为 null。</param>
    /// <returns>解析成功返回 true。</returns>
    public bool TryParse(string? text, [NotNullWhen(true)] out TAddress? address, [NotNullWhen(false)] out string? error)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        address = null;
        if (trimmed.Length == 0)
        {
            error = "The address is empty.";
            return false;
        }

        TDevice? device = FindDevice(trimmed, out int symbolLength);
        if (device is null)
        {
            error = $"Unknown device in address '{trimmed}'.";
            return false;
        }

        return TryParseBody(trimmed, device, trimmed.Substring(symbolLength), out address, out error);
    }

    // 符号之后的部分是 "编号" 或 "编号.位号"。
    private bool TryParseBody(string text, TDevice device, string body, [NotNullWhen(true)] out TAddress? address, [NotNullWhen(false)] out string? error)
    {
        int dot = body.IndexOf('.');
        string numberDigits = dot < 0 ? body : body.Substring(0, dot);
        if (!TryParseNumber(text, device, numberDigits, out int number, out error))
        {
            address = null;
            return false;
        }

        int? bitIndex = null;
        if (dot >= 0)
        {
            if (!TryParseBitIndex(text, device, body.Substring(dot + 1), out int parsedBit, out error))
            {
                address = null;
                return false;
            }

            bitIndex = parsedBit;
        }

        address = create(device, number, bitIndex, text);
        error = null;
        return true;
    }

    private static bool TryParseNumber(string text, TDevice device, string digits, out int number, [NotNullWhen(false)] out string? error)
    {
        if (digits.Length == 0)
        {
            number = 0;
            error = $"The device number is missing in address '{text}'.";
            return false;
        }

        return TryParseValue(text, digits, device.Radix, device.MaxNumber, "device number", out number, out error);
    }

    private bool TryParseBitIndex(string text, TDevice device, string digits, out int bitIndex, [NotNullWhen(false)] out string? error)
    {
        if (device.Unit == PlcAddressUnit.Bit)
        {
            bitIndex = 0;
            error = $"Bit devices do not support a bit index in address '{text}'.";
            return false;
        }

        // 位号上限由单位决定（S2）：字节软元件一个地址 8 位，字软元件 16 位。
        long maximum = device.Unit == PlcAddressUnit.Byte ? MaxByteBitIndex : MaxWordBitIndex;
        return TryParseValue(text, digits, bitIndexRadix, maximum, "bit index", out bitIndex, out error);
    }

    // 编号与位号共用：数字必须全部合法，数值不得超过上限（饱和解析，溢出安全）。
    private static bool TryParseValue(string text, string digits, int radix, long maximum, string subject, out int value, [NotNullWhen(false)] out string? error)
    {
        if (!AreDigits(digits, radix))
        {
            value = 0;
            error = $"'{text}' is not a valid {RadixName(radix)} {subject}.";
            return false;
        }

        long parsed = ParseSaturated(digits, radix, maximum);
        if (parsed > maximum)
        {
            value = 0;
            error = $"The {subject} in address '{text}' must be between 0 and {maximum}.";
            return false;
        }

        value = (int)parsed;
        error = null;
        return true;
    }

    // 按符号长度降序排列后取第一个匹配的符号，即最长前缀。匹配之后不再尝试更短的前缀。
    private TDevice? FindDevice(string text, out int symbolLength)
    {
        foreach (TDevice device in devicesByPrefixLength)
        {
            if (text.StartsWith(device.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                symbolLength = device.Symbol.Length;
                return device;
            }
        }

        symbolLength = 0;
        return default;
    }

    private static TDevice[] SortByPrefixLength(IEnumerable<TDevice> devices)
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var table = new List<TDevice>();
        foreach (TDevice device in devices)
        {
            if (device is null)
                throw new ArgumentException("The device table must not contain null.", nameof(devices));
            if (string.IsNullOrWhiteSpace(device.Symbol) || device.Symbol != device.Symbol.Trim())
                throw new ArgumentException("A device symbol must not be empty or contain surrounding spaces.", nameof(devices));
            if (!symbols.Add(device.Symbol))
                throw new ArgumentException($"The device symbol '{device.Symbol}' is defined more than once.", nameof(devices));
            if (device.Radix is not (DecimalRadix or HexadecimalRadix))
                throw new ArgumentOutOfRangeException(nameof(devices), device.Radix, $"The radix of device '{device.Symbol}' must be 10 or 16.");
            if (device.MaxNumber < 0)
                throw new ArgumentOutOfRangeException(nameof(devices), device.MaxNumber, $"The maximum number of device '{device.Symbol}' must not be negative.");

            table.Add(device);
        }

        return table.OrderByDescending(device => device.Symbol.Length).ToArray();
    }

    // 每个字符都是合法数字（进制内）且至少一个字符。
    private static bool AreDigits(string digits, int radix)
    {
        if (digits.Length == 0)
            return false;

        foreach (char c in digits)
        {
            int value = DigitValue(c);
            if (value < 0 || value >= radix)
                return false;
        }

        return true;
    }

    // 数字已通过 AreDigits 校验。达到 limit + 1 后饱和，既避免溢出，也让调用方能判断超出上限。
    private static long ParseSaturated(string digits, int radix, long limit)
    {
        long value = 0;
        foreach (char c in digits)
            value = Math.Min(value * radix + DigitValue(c), limit + 1);

        return value;
    }

    private static int DigitValue(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'A' && c <= 'F')
            return c - 'A' + FirstHexLetterValue;
        if (c >= 'a' && c <= 'f')
            return c - 'a' + FirstHexLetterValue;

        return -1;
    }

    private static string RadixName(int radix) => radix == HexadecimalRadix ? "hexadecimal" : "decimal";
}
