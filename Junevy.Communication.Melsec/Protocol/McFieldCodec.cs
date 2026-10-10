namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// MC 字段编码策略（P2 设计 Q2）：二进制与 ASCII 共用同一份帧与请求数据代码，两者的差异只在字段的写入与读取方式。
/// 写入到 <see cref="Stream"/>；读取针对调用方已按字段宽度切出的定长切片。
/// </summary>
internal abstract class McFieldCodec
{
    private static readonly McFieldCodec BinaryCodec = new McBinaryFieldCodec();
    private static readonly McFieldCodec AsciiCodec = new McAsciiFieldCodec();

    /// <summary>取得指定编码的字段编码策略（无状态，可共享）。</summary>
    /// <param name="encoding">编码。</param>
    /// <returns>字段编码策略。</returns>
    public static McFieldCodec For(McEncoding encoding) => encoding switch
    {
        McEncoding.Binary => BinaryCodec,
        McEncoding.Ascii => AsciiCodec,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "The MC encoding is not defined."),
    };

    /// <summary>1 字节值的字段宽度：二进制 1 字节，ASCII 2 个字符。</summary>
    public abstract int UInt8Size { get; }

    /// <summary>2 字节值的字段宽度：二进制 2 字节，ASCII 4 个字符。</summary>
    public abstract int UInt16Size { get; }

    /// <summary>软元件编号的上限（含）：二进制为 3 字节；ASCII 十进制软元件为 6 位十进制数字。</summary>
    /// <param name="device">软元件。</param>
    /// <returns>编号上限。</returns>
    public abstract int MaxDeviceNumber(McDevice device);

    /// <summary>写入 1 字节值。</summary>
    public abstract void WriteUInt8(Stream output, byte value);

    /// <summary>写入 2 字节值（二进制小端；ASCII 为 4 位十六进制，高位在前）。</summary>
    public abstract void WriteUInt16(Stream output, ushort value);

    /// <summary>写入副头部（二进制为高位在前的 2 字节；ASCII 为 4 位十六进制；手册 p.42）。</summary>
    public abstract void WriteSubheader(Stream output, ushort value);

    /// <summary>写入软元件的代码与编号，顺序由编码决定。</summary>
    public abstract void WriteDevice(Stream output, McDevice device, int number);

    /// <summary>写入字数据；输入为规范化布局（每字 2 字节小端）。</summary>
    public abstract void WriteWords(Stream output, ReadOnlySpan<byte> littleEndianWords);

    /// <summary>写入位数据（手册 p.72）。</summary>
    public abstract void WriteBits(Stream output, ReadOnlySpan<bool> points);

    /// <summary>字数据的字段宽度（字节或字符）。</summary>
    public abstract int WordFieldSize(int words);

    /// <summary>位数据的字段宽度（字节或字符）。</summary>
    public abstract int BitFieldSize(int points);

    /// <summary>读取副头部。</summary>
    public abstract bool TryReadSubheader(ReadOnlySpan<byte> field, out ushort value);

    /// <summary>读取 2 字节值。</summary>
    public abstract bool TryReadUInt16(ReadOnlySpan<byte> field, out ushort value);

    /// <summary>读取 1 字节值（二进制 1 字节；ASCII 2 个十六进制字符）。</summary>
    public abstract bool TryReadUInt8(ReadOnlySpan<byte> field, out byte value);

    /// <summary>读取字数据到规范化布局（每字 2 字节小端）；字段宽度为 <see cref="WordFieldSize"/>。</summary>
    public abstract bool TryReadWords(ReadOnlySpan<byte> field, Span<byte> littleEndianWords);

    /// <summary>读取位数据，每点 1 个字节（0 或 1）；字段宽度为 <see cref="BitFieldSize"/>。</summary>
    public abstract bool TryReadBits(ReadOnlySpan<byte> field, Span<byte> points);

    /// <summary>从 <paramref name="rest"/> 的开头切出 <paramref name="size"/> 个字节的字段；成功时 <paramref name="rest"/> 前移。</summary>
    public static bool TryTake(ref ReadOnlySpan<byte> rest, int size, out ReadOnlySpan<byte> field)
    {
        if (rest.Length < size)
        {
            field = default;
            return false;
        }

        field = rest.Slice(0, size);
        rest = rest.Slice(size);
        return true;
    }

    /// <summary>切出并读取一个 2 字节值字段（宽度见 <see cref="UInt16Size"/>）。</summary>
    public bool TryTakeUInt16(ref ReadOnlySpan<byte> rest, out ushort value)
    {
        value = 0;
        return TryTake(ref rest, UInt16Size, out ReadOnlySpan<byte> field) && TryReadUInt16(field, out value);
    }

    /// <summary>写入原始字节（回环数据）。两种编码都按字符原样传输（手册 p.185）。</summary>
    public void WriteRaw(Stream output, ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray();
        output.Write(copy, 0, copy.Length);
    }

    /// <summary>编号必须能用本编码表示；超出时抛出（运行期的范围检查由 McClient 的 ValidateRange 先行完成）。</summary>
    protected void RequireDeviceNumber(McDevice device, int number)
    {
        int maximum = MaxDeviceNumber(device);
        if (number < 0 || number > maximum)
            throw new ArgumentOutOfRangeException(nameof(number), number, $"The device number must be between 0 and {maximum} for this encoding.");
    }
}
