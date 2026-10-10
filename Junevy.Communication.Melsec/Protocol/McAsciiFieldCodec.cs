using System.Globalization;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// ASCII 字段编码：数值为大写十六进制文本，高位在前；软元件为 2 字符代码加 6 位编号，编号按软元件进制书写（手册 p.67）；
/// 字数据每字 4 位十六进制（手册 p.73）；位数据每点一个字符，ON 为字符 1，OFF 为字符 0（手册 p.72）。
/// </summary>
internal sealed class McAsciiFieldCodec : McFieldCodec
{
    private const int DeviceNumberDigits = 6;           // 软元件编号的位数（手册 p.67）
    private const int MaxDecimalDeviceNumber = 999999;  // 6 位十进制数字的上限（手册 p.67）
    private const int DecimalRadix = 10;                // 十进制软元件（手册 p.67 的进制列）

    /// <inheritdoc />
    public override int UInt8Size => 2;

    /// <inheritdoc />
    public override int UInt16Size => 4;

    /// <inheritdoc />
    public override int MaxDeviceNumber(McDevice device)
        => device.Radix == DecimalRadix ? MaxDecimalDeviceNumber : device.MaxNumber;

    /// <inheritdoc />
    public override void WriteUInt8(Stream output, byte value) => WriteHex(output, value, 2);

    /// <inheritdoc />
    public override void WriteUInt16(Stream output, ushort value) => WriteHex(output, value, 4);

    /// <inheritdoc />
    public override void WriteSubheader(Stream output, ushort value) => WriteHex(output, value, 4);

    /// <inheritdoc />
    public override void WriteDevice(Stream output, McDevice device, int number)
    {
        RequireDeviceNumber(device, number);
        WriteText(output, device.AsciiCode);
        string digits = device.Radix == DecimalRadix
            ? number.ToString("D" + DeviceNumberDigits, CultureInfo.InvariantCulture)
            : number.ToString("X" + DeviceNumberDigits, CultureInfo.InvariantCulture);
        WriteText(output, digits);
    }

    /// <inheritdoc />
    public override void WriteWords(Stream output, ReadOnlySpan<byte> littleEndianWords)
    {
        for (int i = 0; i < littleEndianWords.Length; i += 2)
            WriteHex(output, (ushort)(littleEndianWords[i] | (littleEndianWords[i + 1] << 8)), 4);
    }

    /// <inheritdoc />
    public override void WriteBits(Stream output, ReadOnlySpan<bool> points)
    {
        foreach (bool point in points)
            output.WriteByte(point ? (byte)'1' : (byte)'0');
    }

    /// <inheritdoc />
    public override int WordFieldSize(int words) => words * 4;

    /// <inheritdoc />
    public override int BitFieldSize(int points) => points;

    /// <inheritdoc />
    public override bool TryReadSubheader(ReadOnlySpan<byte> field, out ushort value) => TryParseHex(field, out value);

    /// <inheritdoc />
    public override bool TryReadUInt16(ReadOnlySpan<byte> field, out ushort value) => TryParseHex(field, out value);

    /// <inheritdoc />
    public override bool TryReadUInt8(ReadOnlySpan<byte> field, out byte value)
    {
        bool valid = TryParseHex(field, out ushort word);
        value = (byte)word;
        return valid;
    }

    /// <inheritdoc />
    public override bool TryReadWords(ReadOnlySpan<byte> field, Span<byte> littleEndianWords)
    {
        for (int i = 0; i < littleEndianWords.Length; i += 2)
        {
            if (!TryParseHex(field.Slice(i * 2, 4), out ushort word))
                return false;

            littleEndianWords[i] = (byte)word;
            littleEndianWords[i + 1] = (byte)(word >> 8);
        }

        return true;
    }

    /// <inheritdoc />
    public override bool TryReadBits(ReadOnlySpan<byte> field, Span<byte> points)
    {
        for (int i = 0; i < points.Length; i++)
        {
            if (field[i] != '0' && field[i] != '1')
                return false;

            points[i] = (byte)(field[i] - '0');
        }

        return true;
    }

    private static void WriteHex(Stream output, uint value, int digits)
        => WriteText(output, value.ToString("X" + digits, CultureInfo.InvariantCulture));

    private static void WriteText(Stream output, string text)
    {
        foreach (char character in text)
            output.WriteByte((byte)character);
    }

    private static bool TryParseHex(ReadOnlySpan<byte> digits, out ushort value)
    {
        int result = 0;
        foreach (byte digit in digits)
        {
            int nibble = HexValue(digit);
            if (nibble < 0)
            {
                value = 0;
                return false;
            }

            result = (result << 4) | nibble;
        }

        value = (ushort)result;
        return true;
    }

    private static int HexValue(byte digit) => digit switch
    {
        >= (byte)'0' and <= (byte)'9' => digit - '0',
        >= (byte)'A' and <= (byte)'F' => digit - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
        _ => -1,
    };
}
