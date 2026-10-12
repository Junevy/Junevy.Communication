using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Secs;

/// <summary>
/// SECS-II 数据项（SEMI E5 第 9 节、P51-D3）：一个格式加一段载荷。<see cref="SecsFormat.List"/> 的载荷是子项，
/// 其余格式的载荷是线上的字节，规范形式为——数值大端、布尔 0x00 / 0x01、字符按 Latin-1（P51-D4）。
/// </summary>
/// <remarks>
/// 实例不可变：工厂方法复制传入的数组，之后修改原数组不会影响已建好的项。构造只把"值"翻译成载荷，
/// 线上那层"格式字节 + 长度字节"由内部的 <c>Protocol/SecsItemCodec</c> 负责。
/// 访问器在格式不符时抛 <see cref="InvalidOperationException"/>，消息里写明实际的格式。
/// </remarks>
public sealed partial class SecsItem
{
    /// <summary>数据项的格式。</summary>
    public SecsFormat Format { get; }

    /// <summary>
    /// 元素个数：列表是子项个数，<c>A</c> 是字符数，<c>B</c> 是字节数，其余是数值个数（空项为 0）。
    /// </summary>
    public int Count { get; }

    /// <summary>子项；非列表项的视图始终为空。</summary>
    public IReadOnlyList<SecsItem> Items { get; }

    internal ReadOnlySpan<byte> Payload => _payload;

    private readonly byte[] _payload;

    private SecsItem(SecsFormat format, byte[] payload, IReadOnlyList<SecsItem> items)
    {
        Format = format;
        _payload = payload;
        Items = items;
        Count = format == SecsFormat.List ? items.Count : payload.Length;
    }

    // 解码器已经拿到线上的字节，长度由它校验（必须是元素宽度的整数倍），不必再走一遍值的往返。
    internal static SecsItem FromPayload(SecsFormat format, byte[] payload)
        => new SecsItem(format, payload, NoItems);

    internal static SecsItem FromItems(IReadOnlyList<SecsItem> items)
        => new SecsItem(SecsFormat.List, NoPayload, items);

    /// <summary>建一个列表。</summary>
    /// <param name="items">子项，不能为 null；数组会被复制。</param>
    /// <returns>列表项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> 或其中某个子项为 null。</exception>
    public static SecsItem L(params SecsItem[] items)
    {
        if (items is null)
            throw new ArgumentNullException(nameof(items));
        for (int index = 0; index < items.Length; index++)
        {
            if (items[index] is null)
                throw new ArgumentNullException(nameof(items), $"The child at index {index} is null.");
        }

        return FromItems((SecsItem[])items.Clone());
    }

    /// <summary>建一个单字节字符项（P51-D4）。</summary>
    /// <param name="value">文本，字符必须在 U+0000–U+00FF 之间。</param>
    /// <returns><c>A</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> 含有超出 Latin-1 的字符。</exception>
    public static SecsItem A(string value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));

        var payload = new byte[value.Length];
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character > Latin1Maximum)
                throw new ArgumentException(
                    $"The character U+{(int)character:X4} at index {index} is outside the single byte range U+0000-U+00FF.",
                    nameof(value));

            payload[index] = (byte)character;
        }

        return FromPayload(SecsFormat.Ascii, payload);
    }

    /// <summary>建一个二进制项。</summary>
    /// <param name="values">字节，数组会被复制。</param>
    /// <returns><c>B</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem B(params byte[] values) => FromPayload(SecsFormat.Binary, CopyOf(values));

    /// <summary>建一个布尔项。</summary>
    /// <param name="values">布尔值，数组会被复制。</param>
    /// <returns><c>BOOLEAN</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem Boolean(params bool[] values)
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));

        var payload = new byte[values.Length];
        for (int index = 0; index < values.Length; index++)
            payload[index] = values[index] ? TrueByte : FalseByte;

        return FromPayload(SecsFormat.Boolean, payload);
    }

    /// <summary>建一个 8 位有符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>I1</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem I1(params sbyte[] values)
        => FromPayload(SecsFormat.I1, Pack(values, OneByte, (span, value) => span[0] = unchecked((byte)value)));

    /// <summary>建一个 16 位有符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>I2</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem I2(params short[] values)
        => FromPayload(SecsFormat.I2, Pack(values, TwoBytes, (span, value) => ByteTransform.WriteInt16(span, value, WireOrder)));

    /// <summary>建一个 32 位有符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>I4</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem I4(params int[] values)
        => FromPayload(SecsFormat.I4, Pack(values, FourBytes, (span, value) => ByteTransform.WriteInt32(span, value, WireOrder)));

    /// <summary>建一个 64 位有符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>I8</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem I8(params long[] values)
        => FromPayload(SecsFormat.I8, Pack(values, EightBytes, (span, value) => ByteTransform.WriteInt64(span, value, WireOrder)));

    /// <summary>建一个 8 位无符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>U1</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem U1(params byte[] values) => FromPayload(SecsFormat.U1, CopyOf(values));

    /// <summary>建一个 16 位无符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>U2</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem U2(params ushort[] values)
        => FromPayload(SecsFormat.U2, Pack(values, TwoBytes, (span, value) => ByteTransform.WriteUInt16(span, value, WireOrder)));

    /// <summary>建一个 32 位无符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>U4</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem U4(params uint[] values)
        => FromPayload(SecsFormat.U4, Pack(values, FourBytes, (span, value) => ByteTransform.WriteUInt32(span, value, WireOrder)));

    /// <summary>建一个 64 位无符号整数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>U8</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem U8(params ulong[] values)
        => FromPayload(SecsFormat.U8, Pack(values, EightBytes, (span, value) => ByteTransform.WriteUInt64(span, value, WireOrder)));

    /// <summary>建一个单精度浮点数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>F4</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem F4(params float[] values)
        => FromPayload(SecsFormat.F4, Pack(values, FourBytes, (span, value) => ByteTransform.WriteSingle(span, value, WireOrder)));

    /// <summary>建一个双精度浮点数项。</summary>
    /// <param name="values">数值，数组会被复制。</param>
    /// <returns><c>F8</c> 项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> 为 null。</exception>
    public static SecsItem F8(params double[] values)
        => FromPayload(SecsFormat.F8, Pack(values, EightBytes, (span, value) => ByteTransform.WriteDouble(span, value, WireOrder)));

    /// <summary>本项的 SML 文本片段，不含消息头与结尾的 <c>.</c>；嵌套列表的子项缩进两个空格。</summary>
    /// <returns>SML 片段。</returns>
    public string ToSml() => SecsSml.Item(this);

    /// <summary>等于 <see cref="ToSml"/>。</summary>
    /// <returns>SML 片段。</returns>
    public override string ToString() => ToSml();
}