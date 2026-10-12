using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// SECS-II 数据项的线格式（SEMI E5 第 9 节、P51-D3、D5）：一个格式字节、1–3 个大端长度字节，正文紧随其后。
/// </summary>
/// <remarks>
/// <para>
/// 格式字节的高 6 位是格式码（<see cref="SecsFormat"/> 的取值），低 2 位是长度字节数，因此同一个格式码
/// 会随长度字节数变化：<c>A</c> 是 0x41 / 0x42 / 0x43，<c>J</c> 是 0x45，<c>C2</c> 是 0x49。
/// 编码取能放下长度的最短长度字节数；解码接受多写的长度字节（V-I-17）。
/// </para>
/// <para>
/// 结构错误一律走 <see cref="CommResult{T}"/> 的失败结果，不抛异常，消息里写明偏移与原因。解码的上限
/// （P51-D5）全部在**切之前**判定，声明了多长的载荷都不会先分配内存。
/// </para>
/// </remarks>
internal static class SecsItemCodec
{
    /// <summary>嵌套深度的上限（P51-D5），超出按非法数据处理。</summary>
    internal const int MaximumDepth = 64;

    /// <summary>长度字节数的上限（1–3），来自格式字节的低 2 位能表示的范围。</summary>
    internal const int MaximumLengthBytes = 3;

    private const int LengthByteCountShift = 2;

    /// <summary>格式字节的低 2 位掩码。</summary>
    private const byte LengthByteCountMask = 0x03;

    /// <summary>0xFFFFFF：3 个长度字节能表示的最大长度。</summary>
    private const int MaximumEncodableLength = 0xFFFFFF;

    /// <summary>每个子项至少要占的字节数：一个格式字节加至少一个长度字节（P51-D5）。</summary>
    private const int MinimumItemBytes = 2;

    /// <summary>写入一项：格式字节 + 长度字节 + 正文（列表写的是逐个子项）。</summary>
    /// <param name="output">目标缓冲。</param>
    /// <param name="item">要写的数据项。</param>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> 或 <paramref name="item"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">列表的子项数或载荷长度超过 0xFFFFFF，3 个长度字节装不下。</exception>
    internal static void Encode(IBufferWriter<byte> output, SecsItem item)
    {
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (item is null)
            throw new ArgumentNullException(nameof(item));

        int length = item.Format == SecsFormat.List ? item.Items.Count : item.Payload.Length;
        int lengthBytes = LengthByteCount(length);

        BigEndianFields.WriteUInt8(output, (byte)((int)item.Format | lengthBytes));
        WriteLength(output, length, lengthBytes);

        if (item.Format == SecsFormat.List)
        {
            foreach (SecsItem child in item.Items)
                Encode(output, child);
            return;
        }

        BigEndianFields.WriteBytes(output, item.Payload);
    }

    /// <summary>读出一项（正文应当恰好是一项；多余的字节被忽略）。</summary>
    /// <param name="body">正文的剩余字节。</param>
    /// <param name="maxLength">允许的最大正文长度（P51-D5 的 <c>MaxMessageLength</c>）。</param>
    /// <returns>数据项；结构不合法时返回 <see cref="CommErrorKind.ProtocolViolation"/> 的失败结果。</returns>
    internal static CommResult<SecsItem> Decode(ReadOnlySpan<byte> body, int maxLength)
    {
        if (body.Length > maxLength)
            return Invalid<SecsItem>($"The body carries {body.Length} byte(s), which is longer than the {maxLength} byte limit.");

        ReadOnlySpan<byte> remaining = body;
        return ReadItem(ref remaining, TopLevelDepth, body.Length);
    }

    /// <summary>
    /// 一个元素在线上占的字节数；<see cref="SecsFormat.List"/> 的长度是子项个数而不是字节数，因此为 0。
    /// 解码用它判断长度是不是元素宽度的整数倍。
    /// </summary>
    /// <param name="format">数据项的格式。</param>
    /// <returns>元素宽度，0–8。</returns>
    internal static int ElementBytes(SecsFormat format) => format switch
    {
        SecsFormat.List => 0,
        SecsFormat.Binary or SecsFormat.Boolean or SecsFormat.Ascii or SecsFormat.I1 or SecsFormat.U1 => 1,
        SecsFormat.I2 or SecsFormat.U2 => 2,
        SecsFormat.I4 or SecsFormat.U4 or SecsFormat.F4 => 4,
        _ => 8,
    };

    /// <summary>顶层项的深度；嵌套每深一层加一。</summary>
    private const int TopLevelDepth = 0;

    private static CommResult<SecsItem> ReadItem(ref ReadOnlySpan<byte> data, int depth, int end)
    {
        if (depth > MaximumDepth)
            return Invalid<SecsItem>($"The nesting depth is {depth}, past the limit of {MaximumDepth}.");

        int offset = end - data.Length;
        if (!BigEndianFields.TryTakeUInt8(ref data, out byte formatByte))
            return Invalid<SecsItem>($"The item at offset {offset} is truncated: the format byte is missing.");

        int lengthBytes = formatByte & LengthByteCountMask;
        if (lengthBytes is 0 or > MaximumLengthBytes)
            return Invalid<SecsItem>($"The item at offset {offset} declares a length byte count of {lengthBytes}, but it must be 1 to {MaximumLengthBytes}.");

        if (!TryParseFormat((SecsFormat)(formatByte & ~LengthByteCountMask), out SecsFormat format))
            return Invalid<SecsItem>($"The format code 0x{formatByte & ~LengthByteCountMask:X2} at offset {offset} is not supported.");

        if (!TryTakeLength(ref data, lengthBytes, out int length))
            return Invalid<SecsItem>($"The item at offset {offset} is truncated: the {lengthBytes} length byte(s) are missing.");

        return format == SecsFormat.List
            ? ReadList(ref data, length, depth, end, offset)
            : ReadScalar(ref data, format, length, offset);
    }

    // 标量要连载荷一起消耗掉：列表的子项是紧挨着写的，游标不前进的话下一个子项就从载荷中间开始读了。
    private static CommResult<SecsItem> ReadScalar(ref ReadOnlySpan<byte> data, SecsFormat format, int length, int offset)
    {
        int elementBytes = ElementBytes(format);
        if (length % elementBytes != 0)
            return Invalid<SecsItem>($"The item at offset {offset} declares {length} byte(s), which is not a whole number of {elementBytes} byte(s) {format} element(s).");
        if (length > data.Length)
            return Invalid<SecsItem>($"The item at offset {offset} is truncated: it declares {length} byte(s) but only {data.Length} byte(s) remain.");

        byte[] payload = data.Slice(0, length).ToArray();
        data = data.Slice(length);
        return CommResult<SecsItem>.Success(SecsItem.FromPayload(format, payload));
    }

    private static CommResult<SecsItem> ReadList(ref ReadOnlySpan<byte> data, int count, int depth, int end, int offset)
    {
        // 声明的子项个数必须先与"剩下多少字节装得下"对照，否则一个损坏的长度就能让解码器按声明去申请数组。
        if (count > data.Length / MinimumItemBytes)
            return Invalid<SecsItem>($"The list at offset {offset} declares {count} child item(s), but each needs at least {MinimumItemBytes} byte(s) and only {data.Length} byte(s) remain.");

        var children = new SecsItem[count];
        for (int index = 0; index < count; index++)
        {
            CommResult<SecsItem> child = ReadItem(ref data, depth + 1, end);
            if (!child.IsSuccess)
                return child;

            children[index] = child.Data;
        }

        return CommResult<SecsItem>.Success(SecsItem.FromItems(children));
    }

    private static bool TryParseFormat(SecsFormat format, out SecsFormat parsed)
    {
        // 枚举值两两不同，因此一次范围判断加排除就能把未分配的码挡在外面（J、C2 也落在未分配区间里）。
        parsed = format;
        return format is SecsFormat.List or SecsFormat.Binary or SecsFormat.Boolean or SecsFormat.Ascii
            or SecsFormat.I8 or SecsFormat.I1 or SecsFormat.I2 or SecsFormat.I4
            or SecsFormat.F8 or SecsFormat.F4
            or SecsFormat.U8 or SecsFormat.U1 or SecsFormat.U2 or SecsFormat.U4;
    }

    private static bool TryTakeLength(ref ReadOnlySpan<byte> data, int lengthBytes, out int length)
    {
        int value = 0;
        for (int index = 0; index < lengthBytes; index++)
        {
            if (!BigEndianFields.TryTakeUInt8(ref data, out byte part))
            {
                length = 0;
                return false;
            }

            value = value << 8 | part;
        }

        length = value;
        return true;
    }

    private static int LengthByteCount(int length)
    {
        if (length < 0 || length > MaximumEncodableLength)
            throw new ArgumentOutOfRangeException(
                "item",
                length,
                $"A SECS-II item must hold at most {MaximumEncodableLength} byte(s) or child item(s) because its length has {MaximumLengthBytes} byte(s).");

        // SEMI E5 第 9 节：长度字段取能表示该长度的最少字节数，最多 3 个。
        if (length <= 0xFF)
            return 1;
        return length <= 0xFFFF ? 2 : MaximumLengthBytes;
    }

    private static void WriteLength(IBufferWriter<byte> output, int length, int lengthBytes)
    {
        switch (lengthBytes)
        {
            case 1:
                BigEndianFields.WriteUInt8(output, (byte)length);
                break;
            case 2:
                BigEndianFields.WriteUInt16(output, (ushort)length);
                break;
            default:
                BigEndianFields.WriteUInt24(output, length);
                break;
        }
    }

    private static CommResult<SecsItem> Invalid<SecsItem>(string message)
        => CommResult<SecsItem>.Fail(message, CommErrorKind.ProtocolViolation);
}