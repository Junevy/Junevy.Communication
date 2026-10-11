using System.Diagnostics.CodeAnalysis;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的地址解析器（P4-D16）：大小写不敏感，可带 <c>%</c> 前缀，数字一律十进制。
/// 数据块形如 <c>DB1.DBX12.3</c>、<c>DB1.DBB12</c>、<c>DB1.DBW12</c>、<c>DB1.DBD12</c>；
/// I、Q、M 区形如 <c>M12.3</c>、<c>MB12</c>、<c>MW12</c>、<c>MD12</c>。
/// X 必须带位号，B、W、D 不能带位号；宽度字母不限制读写长度，长度由调用的方法决定。
/// 范围在入口集中校验：数据块号 1–65535、字节号 0–2097151、位号 0–7。
/// </summary>
/// <remarks>
/// 不复用套件的声明式解析器：数据块地址带两个数字，一张软元件表表达不了（计划的摩擦记录 20.2）。
/// 错误消息的格式与套件解析器一致：空地址为 <c>The address is empty.</c>，其余为 <c>'{原文}' is not a valid S7 address: {原因}.</c>。
/// </remarks>
internal static class S7AddressParser
{
    private const string EmptyMessage = "The address is empty.";
    private const string UnknownArea = "the area must be DB, M, I or Q";
    private const string DataBlockStructure = "a data block address must look like DB1.DBW0";
    private const string DataBlockWidthLetter = "the width letter must be X, B, W or D";
    private const string AreaWidthLetter = "the width letter must be B, W or D";
    private const string RequiresBitIndex = "X requires a bit index, for example DB1.DBX0.7";
    private const string BitIndexNotAllowed = "B, W and D do not take a bit index";
    private const string DataBlockNumber = "data block number";
    private const string ByteNumber = "byte number";
    private const string BitIndex = "bit index";

    private const int DataBlockNumberMinimum = 1;
    private const int DataBlockNumberMaximum = 65535;
    private const int BitIndexMaximum = 7;

    // 宽度写法对位号的规定（P4-D16）：X 必须带位号，B、W、D 不能带，I、Q、M 的直接编号写法（M12.3）可选。
    private enum BitIndexRule
    {
        Optional,
        Forbidden,
        Required,
    }

    /// <summary>
    /// 解析地址文本。
    /// </summary>
    /// <param name="text">地址文本；为 null 时视为空。</param>
    /// <param name="address">解析成功时为地址；失败时为 null。</param>
    /// <param name="error">解析失败时为英文错误消息；成功时为 null。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out S7Address? address, [NotNullWhen(false)] out string? error)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        address = null;
        if (trimmed.Length == 0)
        {
            error = EmptyMessage;
            return false;
        }

        // 可选的百分号前缀只影响原文（Text 保留它），不参与语法判断。
        string body = trimmed[0] == '%' ? trimmed.Substring(1) : trimmed;
        return body.StartsWith("DB", StringComparison.OrdinalIgnoreCase)
            ? TryParseDataBlock(trimmed, body, out address, out error)
            : TryParseArea(trimmed, body, out address, out error);
    }

    // DB 区：DB{n}.DB{X|B|W|D}{b}[.{bit}]
    private static bool TryParseDataBlock(string text, string body, out S7Address? address, out string? error)
    {
        int dot = body.IndexOf('.');
        string numberDigits = dot < 0 ? body.Substring(2) : body.Substring(2, dot - 2);
        if (!TryParseNumber(text, numberDigits, DataBlockNumberMinimum, DataBlockNumberMaximum, DataBlockNumber, out int dbNumber, out string? reason))
        {
            address = null;
            error = Invalid(text, reason);
            return false;
        }

        // 区号之后必须紧跟 ".DB" 与宽度字母。
        string rest = dot < 0 ? string.Empty : body.Substring(dot + 1);
        if (!rest.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
        {
            address = null;
            error = Invalid(text, DataBlockStructure);
            return false;
        }

        if (!TryTakeWidthLetter(rest.Substring(2), out char widthLetter, out string tail))
        {
            address = null;
            error = Invalid(text, DataBlockWidthLetter);
            return false;
        }

        // X 必须带位号，B、W、D 不能带位号。
        BitIndexRule rule = widthLetter == 'X' ? BitIndexRule.Required : BitIndexRule.Forbidden;
        return TryCreate(text, S7DeviceTable.DataBlock, dbNumber, tail, rule, out address, out error);
    }

    // I、Q、M 区：{L}{b}[.{bit}] 或 {L}{B|W|D}{b}
    private static bool TryParseArea(string text, string body, out S7Address? address, out string? error)
    {
        address = null;
        if (body.Length == 0 || !TryDevice(body[0], out S7Device? device))
        {
            error = Invalid(text, UnknownArea);
            return false;
        }

        BitIndexRule rule = BitIndexRule.Optional;
        string rest = body.Substring(1);
        if (rest.Length > 0 && !IsDecimalDigit(rest[0]))
        {
            // 区域字母之后要么直接是编号，要么是宽度字母；X 不属于 I、Q、M 区（P4-D16）。
            if (!IsAreaWidthLetter(rest[0]))
            {
                error = Invalid(text, AreaWidthLetter);
                return false;
            }

            rule = BitIndexRule.Forbidden;
            rest = rest.Substring(1);
        }

        return TryCreate(text, device, dbNumber: 0, rest, rule, out address, out error);
    }

    // 宽度字母之后的尾巴：字节号，可能带 ".位号"。
    private static bool TryCreate(
        string text, S7Device device, int dbNumber, string tail, BitIndexRule rule,
        out S7Address? address, out string? error)
    {
        address = null;
        SplitTail(tail, out string numberDigits, out string? bitDigits);
        if (!TryParseNumber(text, numberDigits, 0, device.MaxNumber, ByteNumber, out int byteNumber, out string? reason))
        {
            error = Invalid(text, reason);
            return false;
        }

        if (bitDigits is null)
        {
            if (rule == BitIndexRule.Required)
            {
                error = Invalid(text, RequiresBitIndex);
                return false;
            }

            address = new S7Address(device, dbNumber, byteNumber, null, text);
            error = null;
            return true;
        }

        if (rule == BitIndexRule.Forbidden)
        {
            error = Invalid(text, BitIndexNotAllowed);
            return false;
        }

        if (!TryParseNumber(text, bitDigits, 0, BitIndexMaximum, BitIndex, out int bitIndex, out reason))
        {
            error = Invalid(text, reason);
            return false;
        }

        address = new S7Address(device, dbNumber, byteNumber, bitIndex, text);
        error = null;
        return true;
    }

    // 取宽度字母（X、B、W、D），返回它并交出剩余部分。
    private static bool TryTakeWidthLetter(string rest, out char widthLetter, out string tail)
    {
        widthLetter = '\0';
        tail = rest;
        if (rest.Length == 0)
            return false;

        char letter = char.ToUpperInvariant(rest[0]);
        if (letter is not ('X' or 'B' or 'W' or 'D'))
            return false;

        widthLetter = letter;
        tail = rest.Substring(1);
        return true;
    }

    // "12.3" 分成 "12" 与 "3"；没有句点时位号为 null。多余的句点留在位号里，由数字校验报错。
    private static void SplitTail(string tail, out string numberDigits, out string? bitDigits)
    {
        int dot = tail.IndexOf('.');
        if (dot < 0)
        {
            numberDigits = tail;
            bitDigits = null;
            return;
        }

        numberDigits = tail.Substring(0, dot);
        bitDigits = tail.Substring(dot + 1);
    }

    // 编号与位号的集中校验：必须有数字、都是十进制数字，且落在范围内。
    private static bool TryParseNumber(
        string text, string digits, int minimum, int maximum, string subject, out int value,
        [NotNullWhen(false)] out string? reason)
    {
        value = 0;
        reason = null;
        if (digits.Length == 0)
        {
            reason = $"the {subject} is missing";
            return false;
        }

        if (!AreDecimalDigits(digits))
        {
            reason = $"the {subject} must contain only digits";
            return false;
        }

        long parsed = ParseSaturated(digits, maximum);
        if (parsed < minimum || parsed > maximum)
        {
            reason = $"the {subject} must be {minimum}–{maximum}";
            return false;
        }

        value = (int)parsed;
        return true;
    }

    private static bool TryDevice(char letter, [NotNullWhen(true)] out S7Device? device)
    {
        device = char.ToUpperInvariant(letter) switch
        {
            'I' => S7DeviceTable.Inputs,
            'Q' => S7DeviceTable.Outputs,
            'M' => S7DeviceTable.Merkers,
            _ => null,
        };
        return device is not null;
    }

    private static bool IsAreaWidthLetter(char letter)
    {
        char upper = char.ToUpperInvariant(letter);
        return upper is 'B' or 'W' or 'D';
    }

    private static bool AreDecimalDigits(string digits)
    {
        foreach (char c in digits)
        {
            if (!IsDecimalDigit(c))
                return false;
        }

        return true;
    }

    // 达到 maximum + 1 后饱和：既不溢出，也让调用方能判断出"超出上限"。
    private static long ParseSaturated(string digits, int maximum)
    {
        long value = 0;
        foreach (char c in digits)
            value = Math.Min(value * 10 + (c - '0'), (long)maximum + 1);

        return value;
    }

    private static bool IsDecimalDigit(char c) => c >= '0' && c <= '9';

    private static string Invalid(string text, string reason) => $"'{text}' is not a valid S7 address: {reason}.";
}
