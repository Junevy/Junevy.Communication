using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// SECS-II 数据项的编解码（计划 Task 3、P51-D3、D4、D5）：
/// 第 17.2 节黄金向量的双向逐字节比较、结构错误的失败结果、以及 1 / 2 / 3 个长度字节的边界往返。
/// </summary>
/// <remarks>
/// 格式字节的低 2 位是长度字节数，因此同一个格式码会随长度字节数变化：`A` 是 0x41（1 字节）、0x42（2 字节）、
/// 0x43（3 字节）；`J` 与 `C2` 是 0x45 / 0x47 与 0x49 / 0x4B。
/// </remarks>
public sealed class SecsItemCodecTests
{
    // 解码用的上限；结构错误必须在这个上限之内返回失败，测试不能依赖"反正会被截断"。
    private const int GenerousLimit = 1024 * 1024;

    private const byte LetterA = 0x41;

    [Theory]
    [InlineData("01 00")]
    [InlineData("A5 02 01 02")]
    [InlineData("A9 02 03 E8")]
    [InlineData("69 02 FF FE")]
    [InlineData("71 04 FF FF FF FF")]
    [InlineData("A1 08 00 00 00 00 00 00 00 01")]
    [InlineData("61 08 FF FF FF FF FF FF FF FF")]
    [InlineData("91 04 3F 80 00 00")]
    [InlineData("81 08 3F F0 00 00 00 00 00 00")]
    [InlineData("25 02 01 00")]
    [InlineData("21 00")]
    [InlineData("65 01 80")]
    [InlineData("B1 00")]
    [InlineData("01 02 01 00 A5 01 07")]
    public void EverySmallVector_SurvivesDecodeAndEncodeUnchanged(string hex)
    {
        SecsItem decoded = decode(hex);

        Assert.Equal(hex, HexVector.ToText(Encode(decoded)));
    }

    [Fact]
    public void V_I_1_AnEmptyList()
    {
        SecsItem item = decode("01 00");

        Assert.Equal(SecsFormat.List, item.Format);
        Assert.Equal(0, item.Count);
        Assert.Empty(item.Items);
    }

    [Fact]
    public void V_I_2_UnsignedBytes()
    {
        SecsItem item = decode("A5 02 01 02");

        Assert.Equal(SecsFormat.U1, item.Format);
        Assert.Equal(new byte[] { 1, 2 }, item.GetU1());
    }

    [Fact]
    public void V_I_3_UnsignedShort()
    {
        SecsItem item = decode("A9 02 03 E8");

        Assert.Equal(SecsFormat.U2, item.Format);
        Assert.Equal(new ushort[] { 1000 }, item.GetU2());
    }

    [Fact]
    public void V_I_4_SignedShort()
    {
        SecsItem item = decode("69 02 FF FE");

        Assert.Equal(SecsFormat.I2, item.Format);
        Assert.Equal(new short[] { -2 }, item.GetI2());
    }

    [Fact]
    public void V_I_5_SignedInt()
    {
        SecsItem item = decode("71 04 FF FF FF FF");

        Assert.Equal(SecsFormat.I4, item.Format);
        Assert.Equal(new[] { -1 }, item.GetI4());
    }

    [Fact]
    public void V_I_6_UnsignedLong()
    {
        SecsItem item = decode("A1 08 00 00 00 00 00 00 00 01");

        Assert.Equal(SecsFormat.U8, item.Format);
        Assert.Equal(new[] { 1UL }, item.GetU8());
    }

    [Fact]
    public void V_I_7_SignedLong()
    {
        SecsItem item = decode("61 08 FF FF FF FF FF FF FF FF");

        Assert.Equal(SecsFormat.I8, item.Format);
        Assert.Equal(new[] { -1L }, item.GetI8());
    }

    [Fact]
    public void V_I_8_SinglePrecision()
    {
        SecsItem item = decode("91 04 3F 80 00 00");

        Assert.Equal(SecsFormat.F4, item.Format);
        Assert.Equal(new[] { 1f }, item.GetF4());
    }

    [Fact]
    public void V_I_9_DoublePrecision()
    {
        SecsItem item = decode("81 08 3F F0 00 00 00 00 00 00");

        Assert.Equal(SecsFormat.F8, item.Format);
        Assert.Equal(new[] { 1d }, item.GetF8());
    }

    [Fact]
    public void V_I_10_BooleansTreatEveryNonZeroByteAsTrue()
    {
        SecsItem item = decode("25 02 01 00");

        Assert.Equal(SecsFormat.Boolean, item.Format);
        Assert.Equal(new[] { true, false }, item.GetBooleans());
        Assert.Equal(new[] { true, true }, decode("25 02 7F FF").GetBooleans());
    }

    [Fact]
    public void V_I_11_AnEmptyBinaryItem()
    {
        SecsItem item = decode("21 00");

        Assert.Equal(SecsFormat.Binary, item.Format);
        Assert.Empty(item.GetBinary());
    }

    [Fact]
    public void V_I_12_AnAsciiItemOfTwoHundredAndFiftySixCharactersUsesTwoLengthBytes()
    {
        const int Characters = 256;
        byte[] encoded = Encode(SecsItem.A(new string('A', Characters)));

        Assert.Equal("42 01 00", HexVector.ToText(encoded).Substring(0, 8));
        Assert.Equal(Characters + 3, encoded.Length);
        Assert.Equal(new string('A', Characters), Decode(LargeAscii("42 01 00", Characters)).Data!.GetString());
    }

    [Fact]
    public void V_I_13_AnAsciiItemOfSixtyFiveThousandFiveHundredAndThirtySixCharactersUsesThreeLengthBytes()
    {
        const int Characters = 65536;
        byte[] encoded = Encode(SecsItem.A(new string('A', Characters)));

        Assert.Equal("43 01 00 00", HexVector.ToText(encoded).Substring(0, 11));
        Assert.Equal(Characters + 4, encoded.Length);
        Assert.Equal(new string('A', Characters), Decode(LargeAscii("43 01 00 00", Characters)).Data!.GetString());
    }

    [Fact]
    public void V_I_14_SignedByte()
    {
        SecsItem item = decode("65 01 80");

        Assert.Equal(SecsFormat.I1, item.Format);
        Assert.Equal(new sbyte[] { -128 }, item.GetI1());
    }

    [Fact]
    public void V_I_15_AnEmptyUnsignedIntArray()
    {
        SecsItem item = decode("B1 00");

        Assert.Equal(SecsFormat.U4, item.Format);
        Assert.Empty(item.GetU4());
    }

    [Fact]
    public void V_I_16_NestedLists()
    {
        SecsItem item = decode("01 02 01 00 A5 01 07");

        Assert.Equal(SecsFormat.List, item.Format);
        Assert.Equal(2, item.Count);
        Assert.Empty(item.Items[0].Items);
        Assert.Equal(new byte[] { 7 }, item.Items[1].GetU1());
    }

    [Fact]
    public void V_I_17_DecodingAcceptsLengthBytesThatAreMoreThanNeeded()
    {
        SecsItem item = decode("42 00 02 41 42");

        Assert.Equal(SecsFormat.Ascii, item.Format);
        Assert.Equal("AB", item.GetString());
    }

    [Theory]
    [InlineData(255, "41 FF")]
    [InlineData(256, "42 01 00")]
    [InlineData(65535, "42 FF FF")]
    [InlineData(65536, "43 01 00 00")]
    public void EncodingChoosesTheShortestLengthByteCount(int characters, string expectedHeader)
    {
        byte[] encoded = Encode(SecsItem.A(new string('A', characters)));

        Assert.Equal(expectedHeader, HexVector.ToText(encoded).Substring(0, expectedHeader.Length));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    public void RoundTripsSurviveTheOneTwoAndThreeLengthByteBoundaries(int characters)
    {
        SecsItem original = SecsItem.A(new string('A', characters));
        CommResult<SecsItem> decoded = Decode(Encode(original));

        Assert.True(decoded.IsSuccess, decoded.ErrorMessage);
        Assert.Equal(characters, decoded.Data.Count);
        Assert.Equal(original.GetString(), decoded.Data.GetString());
    }

    [Fact]
    public void EncodingAnItemTooLongForThreeLengthBytesIsRejected()
    {
        // 3 个长度字节最多表示 0xFFFFFF 个字节；0x1000001 个字节的 B 项装不下。
        ArgumentOutOfRangeException error =
            Assert.Throws<ArgumentOutOfRangeException>(() => Encode(SecsItem.B(new byte[0x1000001])));
        Assert.Equal("item", error.ParamName);
    }

    [Theory]
    [InlineData("00 00")]
    [InlineData("40 41 42")]
    [InlineData("20 41")]
    public void DecodingRejectsALengthByteCountOfZero(string hex)
    {
        AssertFails(hex, "length byte count");
    }

    [Fact]
    public void DecodingRejectsALengthThatIsNotAWholeNumberOfElements()
    {
        // U2 的长度 3 不是 2 的整数倍。
        AssertFails("A9 03 01 02 03", "not a whole number of 2 byte(s) U2");
    }

    [Theory]
    [InlineData("A9 04 01")]
    [InlineData("01 01 A5 02 01")]
    [InlineData("A5")]
    public void DecodingRejectsATruncatedPayload(string hex)
    {
        AssertFails(hex, "truncated");
    }

    [Theory]
    [InlineData("3E 01 00")]
    [InlineData("45 00 02 41 42")]
    [InlineData("49 00 02 41 42")]
    public void DecodingRejectsAFormatCodeThatIsNotSupported(string hex)
    {
        // 前一个是未分配的格式码；后两个是 P51-D3 明确不支持的 J（JIS-8）与 C2（2 字节字符）。
        AssertFails(hex, "not supported");
    }

    [Fact]
    public void DecodingRejectsAListThatDeclaresMoreChildrenThanCouldFit()
    {
        // P51-D5：每个子项至少 2 字节（格式字节 + 至少 1 个长度字节），剩下 4 个字节装不下 3 个子项。
        AssertFails("01 03 01 00 01 00", "at least");
    }

    [Fact]
    public void DecodingRejectsNestingDeeperThanTheLimit()
    {
        // P51-D5：嵌套深度上限 64。每一层写成"一个子项的列表"，第 65 层必须失败。
        CommResult<SecsItem> tooDeep = Decode(Nested(SecsItemCodec.MaximumDepth + 1));
        AssertFails(tooDeep, "nesting depth");

        // 正好 64 层，最内层是一个空列表，必须成功。
        byte[] deepest = Nested(SecsItemCodec.MaximumDepth);
        CommResult<SecsItem> allowed = Decode(deepest);
        Assert.True(allowed.IsSuccess, allowed.ErrorMessage);
        Assert.Equal(SecsFormat.List, allowed.Data.Format);
    }

    [Fact]
    public void DecodingRejectsABodyLongerThanTheConfiguredLimit()
    {
        AssertFails(SecsItemCodec.Decode(HexVector.ToBytes("A5 02 01 02"), 3), "longer than");

        // 正好等于上限不算超限。
        Assert.True(SecsItemCodec.Decode(HexVector.ToBytes("A5 02 01 02"), 4).IsSuccess);
    }

    [Fact]
    public void EveryFailureIsAProtocolViolationAndSaysWhereItHappened()
    {
        CommResult<SecsItem> result = Decode(HexVector.ToBytes("01 01 3E 01 00"));

        AssertFails(result, "not supported");
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Contains("offset 2", result.ErrorMessage);
    }

    private static SecsItem decode(string hex) => Decode(HexVector.ToBytes(hex)).Data!;

    private static CommResult<SecsItem> Decode(byte[] body) => SecsItemCodec.Decode(body, GenerousLimit);

    private static byte[] Encode(SecsItem item)
    {
        using var buffer = new ByteBufferWriter();
        SecsItemCodec.Encode(buffer, item);
        return buffer.ToArray();
    }

    private static void AssertFails(string hex, string expectedFragment)
        => AssertFails(Decode(HexVector.ToBytes(hex)), expectedFragment);

    private static void AssertFails(CommResult<SecsItem> result, string expectedFragment)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Contains(expectedFragment, result.ErrorMessage);
    }

    // 65536 个字符的向量按十六进制文本拼要几兆字符串，直接按字节拼。
    private static byte[] LargeAscii(string header, int characters)
    {
        byte[] prefix = HexVector.ToBytes(header);
        var body = new byte[prefix.Length + characters];
        prefix.CopyTo(body, 0);
        for (int index = 0; index < characters; index++)
            body[prefix.Length + index] = LetterA;

        return body;
    }

    // 每层写"一个子项的列表"，最内层是一个空列表：n 层共 2n + 2 个字节。
    private static byte[] Nested(int depth)
    {
        byte[] unit = HexVector.ToBytes("01 01");
        byte[] leaf = HexVector.ToBytes("01 00");
        var body = new byte[(unit.Length * depth) + leaf.Length];
        for (int level = 0; level < depth; level++)
            unit.CopyTo(body, level * unit.Length);
        leaf.CopyTo(body, unit.Length * depth);

        return body;
    }
}