namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 二进制字段编码：多字节数值小端；副头部为高位在前的 2 字节（手册 p.42）；软元件为编号 3 字节小端加代码 1 字节（手册 p.67）；
/// 位数据每点 4 位，首点在高半字节，奇数点时最低半字节为 0（手册 p.72）。
/// </summary>
internal sealed class McBinaryFieldCodec : McFieldCodec
{
    private const int HighNibble = 0x10;       // 位数据的首点在高半字节（手册 p.72）
    private const int LowNibble = 0x01;        // 位数据的第二个点在低半字节（手册 p.72）
    private const int NibbleMask = 0x0F;       // 取出低半字节（手册 p.72）
    private const int HighNibbleShift = 4;     // 取出高半字节（手册 p.72）

    /// <inheritdoc />
    public override int UInt8Size => 1;

    /// <inheritdoc />
    public override int UInt16Size => 2;

    /// <inheritdoc />
    public override int MaxDeviceNumber(McDevice device) => device.MaxNumber;

    /// <inheritdoc />
    public override void WriteUInt8(Stream output, byte value) => output.WriteByte(value);

    /// <inheritdoc />
    public override void WriteUInt16(Stream output, ushort value)
    {
        output.WriteByte((byte)value);
        output.WriteByte((byte)(value >> 8));
    }

    /// <inheritdoc />
    public override void WriteSubheader(Stream output, ushort value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    /// <inheritdoc />
    public override void WriteDevice(Stream output, McDevice device, int number)
    {
        RequireDeviceNumber(device, number);
        output.WriteByte((byte)number);
        output.WriteByte((byte)(number >> 8));
        output.WriteByte((byte)(number >> 16));
        output.WriteByte(device.BinaryCode);
    }

    /// <inheritdoc />
    public override void WriteWords(Stream output, ReadOnlySpan<byte> littleEndianWords) => WriteRaw(output, littleEndianWords);

    /// <inheritdoc />
    public override void WriteBits(Stream output, ReadOnlySpan<bool> points)
    {
        for (int i = 0; i < points.Length; i += 2)
        {
            int high = points[i] ? HighNibble : 0;
            int low = i + 1 < points.Length && points[i + 1] ? LowNibble : 0;
            output.WriteByte((byte)(high | low));
        }
    }

    /// <inheritdoc />
    public override int WordFieldSize(int words) => words * UInt16Size;

    /// <inheritdoc />
    public override int BitFieldSize(int points) => (points + 1) / 2;

    /// <inheritdoc />
    public override bool TryReadSubheader(ReadOnlySpan<byte> field, out ushort value)
    {
        value = (ushort)((field[0] << 8) | field[1]);
        return true;
    }

    /// <inheritdoc />
    public override bool TryReadUInt16(ReadOnlySpan<byte> field, out ushort value)
    {
        value = (ushort)(field[0] | (field[1] << 8));
        return true;
    }

    /// <inheritdoc />
    public override bool TryReadUInt8(ReadOnlySpan<byte> field, out byte value)
    {
        value = field[0];
        return true;
    }

    /// <inheritdoc />
    public override bool TryReadWords(ReadOnlySpan<byte> field, Span<byte> littleEndianWords)
    {
        field.CopyTo(littleEndianWords);
        return true;
    }

    /// <inheritdoc />
    public override bool TryReadBits(ReadOnlySpan<byte> field, Span<bool> points)
    {
        for (int i = 0; i < points.Length; i++)
        {
            int nibble = i % 2 == 0 ? field[i / 2] >> HighNibbleShift : field[i / 2] & NibbleMask;
            if (nibble > 1)
                return false;

            points[i] = nibble == 1;
        }

        return true;
    }
}
