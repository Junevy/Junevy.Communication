using System.Text;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Siemens.Protocol;

/// <summary>
/// S7 的字符串字段（计划第 13 节第 3 条）：<c>String</c> 的头部是 2 字节（声明的最大长度、实际长度），字符按
/// <see cref="Junevy.Communication.Plc.PlcDataFormat.StringEncoding"/> 编码；<c>WString</c> 的头部是 4 字节（大端的最大长度与实际长度），字符固定 UTF-16 大端。
/// 写入只发头部与实际字符，字段余下的字节由设备保持原值；读取取整个字段后按头部的实际长度截断。
/// </summary>
internal static class S7StringFormat
{
    /// <summary><c>String</c> 头部的字节数。</summary>
    public const int HeaderBytes = 2;

    /// <summary><c>WString</c> 头部的字节数。</summary>
    public const int WideHeaderBytes = 4;

    /// <summary><c>WString</c> 一个字符占的字节数。</summary>
    public const int BytesPerCharacter = 2;

    /// <summary><c>String</c> 的最大字符数：长度字段只有 1 字节，S7 约定上限 254。</summary>
    public const int MaximumCharacters = 254;

    /// <summary><c>WString</c> 的最大字符数：头部之外还要放得下 2 字节的长度字段，按字符对折算为 16382。</summary>
    public const int MaximumWideCharacters = 16382;

    private const int MinimumCharacters = 1;
    private const int BytePairHighShift = 8;

    private const string MissingValueMessage = "The string value is required.";

    /// <summary><c>String</c> 字段占用的字节数：读请求按它取整个字段。</summary>
    /// <param name="maxLength">字段声明的最大字符数，1–254。</param>
    /// <returns>字段字节数；长度不合法时为 <see cref="CommErrorKind.InvalidRequest"/>。</returns>
    public static CommResult<int> FieldBytes(int maxLength) => FieldBytes(maxLength, MaximumCharacters, HeaderBytes, 1);

    /// <summary><c>WString</c> 字段占用的字节数：读请求按它取整个字段。</summary>
    /// <param name="maxLength">字段声明的最大字符数，1–16382。</param>
    /// <returns>字段字节数；长度不合法时为 <see cref="CommErrorKind.InvalidRequest"/>。</returns>
    public static CommResult<int> WideFieldBytes(int maxLength) => FieldBytes(maxLength, MaximumWideCharacters, WideHeaderBytes, BytesPerCharacter);

    /// <summary>编码 <c>String</c> 的写入内容：头部 + 实际字符，字段余下的字节不写。</summary>
    /// <param name="value">字符串；为 null 时返回 <see cref="CommErrorKind.InvalidRequest"/>。</param>
    /// <param name="maxLength">字段声明的最大字符数。</param>
    /// <param name="encoding">字符编码（取自客户端的数据格式）。</param>
    /// <returns>要写入的字节；编码结果超过 <paramref name="maxLength"/> 时返回 <see cref="CommErrorKind.InvalidRequest"/>。</returns>
    public static CommResult<byte[]> Encode(string? value, int maxLength, Encoding encoding)
    {
        CommResult<int> field = FieldBytes(maxLength);
        if (!field.IsSuccess)
            return field.As<byte[]>();

        if (value is null)
            return Invalid<byte[]>(MissingValueMessage);

        byte[] characters = encoding.GetBytes(value);
        if (characters.Length > maxLength)
            return Invalid<byte[]>($"The value encodes to {characters.Length} byte(s), which does not fit a String[{maxLength}] field.");

        using var buffer = new ByteBufferWriter(field.Data);
        S7Fields.WriteUInt8(buffer, (byte)maxLength);
        S7Fields.WriteUInt8(buffer, (byte)characters.Length);
        S7Fields.WriteRaw(buffer, characters);
        return CommResult<byte[]>.Success(buffer.ToArray());
    }

    /// <summary>编码 <c>WString</c> 的写入内容：头部 + 实际字符，字段余下的字节不写。</summary>
    /// <param name="value">字符串；为 null 时返回 <see cref="CommErrorKind.InvalidRequest"/>。</param>
    /// <param name="maxLength">字段声明的最大字符数。</param>
    /// <returns>要写入的字节；字符数超过 <paramref name="maxLength"/> 时返回 <see cref="CommErrorKind.InvalidRequest"/>。</returns>
    public static CommResult<byte[]> EncodeWide(string? value, int maxLength)
    {
        CommResult<int> field = WideFieldBytes(maxLength);
        if (!field.IsSuccess)
            return field.As<byte[]>();

        if (value is null)
            return Invalid<byte[]>(MissingValueMessage);
        if (value.Length > maxLength)
            return Invalid<byte[]>($"The value has {value.Length} character(s), which does not fit a WString[{maxLength}] field.");

        using var buffer = new ByteBufferWriter(field.Data);
        S7Fields.WriteUInt16(buffer, (ushort)maxLength);
        S7Fields.WriteUInt16(buffer, (ushort)value.Length);
        foreach (char character in value)
            S7Fields.WriteUInt16(buffer, character);

        return CommResult<byte[]>.Success(buffer.ToArray());
    }

    /// <summary>解码 <c>String</c> 字段：按头部的实际长度截断，字段余下的字节不参与结果。</summary>
    /// <param name="field">整个字段（头部 + <paramref name="maxLength"/> 个字符）。</param>
    /// <param name="maxLength">调用方声明的最大字符数，即读取时使用的值。</param>
    /// <param name="encoding">字符编码（取自客户端的数据格式）。</param>
    /// <returns>字符串；头部的实际长度大于声明的最大长度或大于 <paramref name="maxLength"/> 时为 <see cref="CommErrorKind.ProtocolViolation"/>。</returns>
    public static CommResult<string> Decode(byte[] field, int maxLength, Encoding encoding)
    {
        CommResult header = CheckLengths(field[0], field[1], maxLength, "String");
        if (!header.IsSuccess)
            return header.As<string>();

        return CommResult<string>.Success(encoding.GetString(field, HeaderBytes, field[1]));
    }

    /// <summary>解码 <c>WString</c> 字段：把每两个字节还原为一个 UTF-16 码字。</summary>
    /// <param name="field">整个字段（头部 + <paramref name="maxLength"/> 个字符）。</param>
    /// <param name="maxLength">调用方声明的最大字符数，即读取时使用的值。</param>
    /// <returns>字符串；头部的实际长度大于声明的最大长度或大于 <paramref name="maxLength"/> 时为 <see cref="CommErrorKind.ProtocolViolation"/>。</returns>
    public static CommResult<string> DecodeWide(byte[] field, int maxLength)
    {
        int declaredMaximum = TakeUInt16(field, 0);
        int actual = TakeUInt16(field, 2);
        CommResult header = CheckLengths(declaredMaximum, actual, maxLength, "WString");
        if (!header.IsSuccess)
            return header.As<string>();

        var characters = new char[actual];
        for (int i = 0; i < actual; i++)
            characters[i] = (char)TakeUInt16(field, WideHeaderBytes + BytesPerCharacter * i);

        return CommResult<string>.Success(new string(characters));
    }

    // 字段长度 = 头部 + 每字符字节数 × maxLength。
    private static CommResult<int> FieldBytes(int maxLength, int maximumCharacters, int headerBytes, int bytesPerCharacter)
    {
        if (maxLength < MinimumCharacters || maxLength > maximumCharacters)
            return Invalid<int>($"The string length must be between {MinimumCharacters} and {maximumCharacters}.");

        return CommResult<int>.Success(headerBytes + bytesPerCharacter * maxLength);
    }

    // 头部的两个长度都要检查：设备写的最大长度可能小于实际长度，也可能实际长度超出调用方读到的字段。
    private static CommResult CheckLengths(int declaredMaximum, int actual, int maxLength, string format)
    {
        if (actual > declaredMaximum)
            return S7Errors.Violation($"The {format} field declares a maximum of {declaredMaximum} character(s) but carries {actual}.");
        if (actual > maxLength)
            return S7Errors.Violation($"The {format} field carries {actual} character(s), more than the {maxLength} requested.");

        return CommResult.Success();
    }

    // 字段一定包含完整的头部：读请求按 FieldBytes 取整个字段，长度由套件在解析应答时校验。
    private static int TakeUInt16(byte[] field, int index) => field[index] << BytePairHighShift | field[index + 1];

    private static CommResult<T> Invalid<T>(string message) => CommResult<T>.Fail(message, CommErrorKind.InvalidRequest);
}
