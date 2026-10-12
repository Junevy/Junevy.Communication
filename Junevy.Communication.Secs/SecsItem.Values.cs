using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Secs;

/// <summary>
/// <see cref="SecsItem"/> 的取值访问器，以及载荷的打包与拆包。
/// </summary>
/// <remarks>
/// 数值一律大端（P51-D3），复用 Core 的 <see cref="ByteTransform"/>——它的 <see cref="WordOrder.ABCD"/>
/// 就是原样的大端，而且已经处理了 net472 缺少 <c>BitConverter.SingleToInt32Bits</c> 的问题，
/// 本库不必再抄一份浮点数的位模式视图。
/// </remarks>
public sealed partial class SecsItem
{
    // Span<T> 是 ref struct，不能当泛型类型实参，因此这两个委托要自己声明（作为参数类型是允许的）。
    private delegate void ElementWriter<T>(Span<byte> destination, T value);

    private delegate T ElementReader<T>(ReadOnlySpan<byte> source);

    private const byte TrueByte = 0x01;
    private const byte FalseByte = 0x00;

    /// <summary>Latin-1 的上界；<c>A</c> 是单字节字符（P51-D4）。</summary>
    private const char Latin1Maximum = (char)0xFF;

    private const int OneByte = 1;
    private const int TwoBytes = 2;
    private const int FourBytes = 4;
    private const int EightBytes = 8;

    /// <summary>SECS-II 的数值一律大端，<see cref="WordOrder.ABCD"/> 即原样的大端。</summary>
    private static readonly WordOrder WireOrder = WordOrder.ABCD;

    private static readonly byte[] NoPayload = Array.Empty<byte>();

    private static readonly IReadOnlyList<SecsItem> NoItems = Array.Empty<SecsItem>();

    /// <summary>取 <c>A</c> 的文本，每个字节对应一个字符（Latin-1）。</summary>
    /// <returns>文本。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>A</c>。</exception>
    public string GetString()
    {
        Require(SecsFormat.Ascii);
        var characters = new char[_payload.Length];
        for (int index = 0; index < characters.Length; index++)
            characters[index] = (char)_payload[index];

        return new string(characters);
    }

    /// <summary>取 <c>B</c> 的字节。</summary>
    /// <returns>字节的只读视图；调用方改动返回值不会影响本项。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>B</c>。</exception>
    public IReadOnlyList<byte> GetBinary()
    {
        Require(SecsFormat.Binary);
        return Unpack(_payload, OneByte, span => span[0]);
    }

    /// <summary>取 <c>BOOLEAN</c> 的值，线上非 0 即真。</summary>
    /// <returns>布尔值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>BOOLEAN</c>。</exception>
    public IReadOnlyList<bool> GetBooleans()
    {
        Require(SecsFormat.Boolean);
        return Unpack(_payload, OneByte, span => span[0] != FalseByte);
    }

    /// <summary>取 <c>I1</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>I1</c>。</exception>
    public IReadOnlyList<sbyte> GetI1()
    {
        Require(SecsFormat.I1);
        return Unpack(_payload, OneByte, span => unchecked((sbyte)span[0]));
    }

    /// <summary>取 <c>I2</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>I2</c>。</exception>
    public IReadOnlyList<short> GetI2()
    {
        Require(SecsFormat.I2);
        return Unpack(_payload, TwoBytes, span => ByteTransform.ToInt16(span, WireOrder));
    }

    /// <summary>取 <c>I4</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>I4</c>。</exception>
    public IReadOnlyList<int> GetI4()
    {
        Require(SecsFormat.I4);
        return Unpack(_payload, FourBytes, span => ByteTransform.ToInt32(span, WireOrder));
    }

    /// <summary>取 <c>I8</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>I8</c>。</exception>
    public IReadOnlyList<long> GetI8()
    {
        Require(SecsFormat.I8);
        return Unpack(_payload, EightBytes, span => ByteTransform.ToInt64(span, WireOrder));
    }

    /// <summary>取 <c>U1</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>U1</c>。</exception>
    public IReadOnlyList<byte> GetU1()
    {
        Require(SecsFormat.U1);
        return Unpack(_payload, OneByte, span => span[0]);
    }

    /// <summary>取 <c>U2</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>U2</c>。</exception>
    public IReadOnlyList<ushort> GetU2()
    {
        Require(SecsFormat.U2);
        return Unpack(_payload, TwoBytes, span => ByteTransform.ToUInt16(span, WireOrder));
    }

    /// <summary>取 <c>U4</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>U4</c>。</exception>
    public IReadOnlyList<uint> GetU4()
    {
        Require(SecsFormat.U4);
        return Unpack(_payload, FourBytes, span => ByteTransform.ToUInt32(span, WireOrder));
    }

    /// <summary>取 <c>U8</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>U8</c>。</exception>
    public IReadOnlyList<ulong> GetU8()
    {
        Require(SecsFormat.U8);
        return Unpack(_payload, EightBytes, span => ByteTransform.ToUInt64(span, WireOrder));
    }

    /// <summary>取 <c>F4</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>F4</c>。</exception>
    public IReadOnlyList<float> GetF4()
    {
        Require(SecsFormat.F4);
        return Unpack(_payload, FourBytes, span => ByteTransform.ToSingle(span, WireOrder));
    }

    /// <summary>取 <c>F8</c> 的值。</summary>
    /// <returns>数值的只读视图。</returns>
    /// <exception cref="InvalidOperationException">本项不是 <c>F8</c>。</exception>
    public IReadOnlyList<double> GetF8()
    {
        Require(SecsFormat.F8);
        return Unpack(_payload, EightBytes, span => ByteTransform.ToDouble(span, WireOrder));
    }

    // 把值按元素宽度打成载荷；写函数由 ByteTransform 的大端实现承担，因此每个工厂只有一行。
    private static byte[] Pack<T>(T[] values, int elementBytes, ElementWriter<T> write)
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));

        var payload = new byte[values.Length * elementBytes];
        for (int index = 0; index < values.Length; index++)
            write(payload.AsSpan(index * elementBytes, elementBytes), values[index]);

        return payload;
    }

    private static T[] Unpack<T>(ReadOnlySpan<byte> payload, int elementBytes, ElementReader<T> read)
    {
        var values = new T[payload.Length / elementBytes];
        for (int index = 0; index < values.Length; index++)
            values[index] = read(payload.Slice(index * elementBytes, elementBytes));

        return values;
    }

    private static byte[] CopyOf(byte[] values)
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));

        return (byte[])values.Clone();
    }

    // 消息里写明实际的格式，否则调用方只看到一个 InvalidOperationException，不知道自己拿错了访问器。
    private void Require(SecsFormat expected)
    {
        if (Format != expected)
            throw new InvalidOperationException($"The item format is {Format}, not {expected}.");
    }
}