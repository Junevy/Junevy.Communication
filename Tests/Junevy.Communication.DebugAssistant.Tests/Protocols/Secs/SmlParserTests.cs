using Junevy.Communication.DebugAssistant.Protocols.Secs;
using Junevy.Communication.Secs;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Secs;

/// <summary>
/// SML 解析器（计划 Task 15 第 2 条，DA-D13）：每种格式、空列表、多层嵌套、A 中的 <c>\xHH</c>、浮点数的 G9 / G17 都满足
/// <c>Parse(item.ToSml()).ToSml() == item.ToSml()</c>；解析错误给出行号与列号。
/// </summary>
public sealed class SmlParserTests
{
    private const int RandomSeed = 20261020;
    private const int RandomCases = 300;
    private const int MaximumDepth = 3;

    [Fact]
    public void TheSampleOfSection17_3_RoundTripsLineForLine()
    {
        SecsItem item = SecsItem.L(SecsItem.A("EQ01"), SecsItem.A("1.0.0"));
        const string text = "<L [2]\n  <A \"EQ01\">\n  <A \"1.0.0\">\n>";

        Assert.Equal(text, item.ToSml());
        Assert.Equal(text, SmlParser.ParseItem(text).ToSml());
    }

    [Fact]
    public void EveryFormat_RoundTripsItsText()
    {
        SecsItem[] items =
        [
            SecsItem.Boolean(true, false),
            SecsItem.B(0x00, 0x80, 0xFF),
            SecsItem.A("EQ01"),
            SecsItem.A(string.Empty),
            SecsItem.I1(sbyte.MinValue, sbyte.MaxValue),
            SecsItem.I2(short.MinValue, short.MaxValue),
            SecsItem.I4(int.MinValue, -1, int.MaxValue),
            SecsItem.I8(long.MinValue, long.MaxValue),
            SecsItem.U1(0, byte.MaxValue),
            SecsItem.U2(ushort.MaxValue),
            SecsItem.U4(1001, uint.MaxValue),
            SecsItem.U8(ulong.MaxValue),
            SecsItem.F4(1.5f, -0.0f, float.Epsilon, float.MaxValue),
            SecsItem.F8(Math.PI, double.NaN, double.NegativeInfinity),
        ];

        foreach (SecsItem item in items)
        {
            string text = item.ToSml();
            Assert.Equal(text, SmlParser.ParseItem(text).ToSml());
        }
    }

    [Fact]
    public void EmptyValuesAndEmptyLists_RoundTrip()
    {
        Assert.Equal("<U4>", SecsItem.U4().ToSml());
        Assert.Equal("<L [0]>", SecsItem.L().ToSml());

        Assert.Equal("<U4>", SmlParser.ParseItem("<U4>").ToSml());
        Assert.Equal("<L [0]>", SmlParser.ParseItem("<L [0]>").ToSml());
    }

    [Fact]
    public void AsciiEscapes_RoundTrip()
    {
        SecsItem item = SecsItem.A("say \"hi\" \\ \u0001 \u007F \u00FF");
        string text = item.ToSml();

        Assert.Contains("\\x01", text);
        Assert.Contains("\\x7F", text);
        Assert.Contains("\\xFF", text);
        Assert.Equal(text, SmlParser.ParseItem(text).ToSml());
        Assert.Equal("say \"hi\" \\ \u0001 \u007F \u00FF", SmlParser.ParseItem(text).GetString());
    }

    [Fact]
    public void NestedLists_RoundTripAtEveryLevel()
    {
        SecsItem item = SecsItem.L(
            SecsItem.U4(1),
            SecsItem.L(SecsItem.L(SecsItem.B(0x01)), SecsItem.L()),
            SecsItem.A("x"));
        string text = item.ToSml();

        Assert.Equal(text, SmlParser.ParseItem(text).ToSml());
    }

    [Fact]
    public void FloatingPoint_ReadsBackTheValueOfItsG9AndG17Text()
    {
        string single = SecsItem.F4(3.1415927f).ToSml();
        string @double = SecsItem.F8(Math.PI).ToSml();

        Assert.Equal(single, SmlParser.ParseItem(single).ToSml());
        Assert.Equal(new[] { 3.1415927f }, SmlParser.ParseItem(single).GetF4());
        Assert.Equal(@double, SmlParser.ParseItem(@double).ToSml());
        Assert.Equal(new[] { Math.PI }, SmlParser.ParseItem(@double).GetF8());
    }

    [Fact]
    public void SpecialFloatingPoint_ParsesToTheValueTheLibraryWrites()
    {
        Assert.Equal(SecsItem.F8(double.PositiveInfinity).ToSml(), SmlParser.ParseItem("<F8 ∞>").ToSml());
        Assert.Equal(SecsItem.F8(double.NegativeInfinity).ToSml(), SmlParser.ParseItem("<F8 -Infinity>").ToSml());
        Assert.Equal(SecsItem.F8(double.NaN).ToSml(), SmlParser.ParseItem("<F8 NaN>").ToSml());
    }

    [Fact]
    public void RandomTrees_RoundTripToTheSameText()
    {
        var random = new Random(RandomSeed);
        for (int index = 0; index < RandomCases; index++)
        {
            string text = RandomItem(random, 0).ToSml();

            Assert.Equal(text, SmlParser.ParseItem(text).ToSml());
        }
    }

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData("<U1 300>", 1, 5)]
    [InlineData("<U4 4294967296>", 1, 5)]
    [InlineData("<X 1>", 1, 2)]
    [InlineData("<U1 1>x", 1, 7)]
    [InlineData("<U1 1", 1, 6)]
    [InlineData("<L [x]>", 1, 5)]
    [InlineData("<L [2]\n  <U1 1>\n>", 3, 1)]
    [InlineData("<L [1]\n  <J 0x00>\n>", 2, 4)]
    [InlineData("<A \"abc>", 1, 9)]
    [InlineData("<A \"a\\q\">", 1, 6)]
    [InlineData("<A \"\\x4\">", 1, 5)]
    [InlineData("<B 0x1G>", 1, 4)]
    [InlineData("<BOOLEAN X>", 1, 10)]
    public void Errors_CarryTheLineAndColumn(string text, int line, int column)
    {
        SmlParseException error = Assert.Throws<SmlParseException>(() => SmlParser.ParseItem(text));

        Assert.Equal((line, column), (error.Line, error.Column));
        Assert.StartsWith($"第 {line} 行第 {column} 列：", error.Message);
    }

    [Theory]
    [InlineData("<J 0x00>")]
    [InlineData("<C2 \"x\">")]
    public void UnsupportedFormats_AreReportedAsSuch(string text)
    {
        SmlParseException error = Assert.Throws<SmlParseException>(() => SmlParser.ParseItem(text));

        Assert.Contains("不支持", error.Message);
    }

    private static SecsItem RandomItem(Random random, int depth)
    {
        int kind = random.Next(depth < MaximumDepth ? 0 : 1, 14);
        return kind switch
        {
            0 => SecsItem.L(Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomItem(random, depth + 1)).ToArray()),
            1 => SecsItem.A(RandomText(random)),
            2 => SecsItem.B(RandomBytes(random)),
            3 => SecsItem.Boolean(Enumerable.Range(0, random.Next(0, 4)).Select(_ => random.Next(2) == 1).ToArray()),
            4 => SecsItem.I1(Enumerable.Range(0, random.Next(0, 4)).Select(_ => (sbyte)random.Next(sbyte.MinValue, sbyte.MaxValue + 1)).ToArray()),
            5 => SecsItem.I2(Enumerable.Range(0, random.Next(0, 4)).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1)).ToArray()),
            6 => SecsItem.I4(Enumerable.Range(0, random.Next(0, 4)).Select(_ => random.Next(int.MinValue, int.MaxValue)).ToArray()),
            7 => SecsItem.I8(Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomSigned(random)).ToArray()),
            8 => SecsItem.U1(Enumerable.Range(0, random.Next(0, 4)).Select(_ => (byte)random.Next(256)).ToArray()),
            9 => SecsItem.U2(Enumerable.Range(0, random.Next(0, 4)).Select(_ => (ushort)random.Next(65536)).ToArray()),
            10 => SecsItem.U4(Enumerable.Range(0, random.Next(0, 4)).Select(_ => (uint)random.NextInt64(0, 1L << 32)).ToArray()),
            11 => SecsItem.U8(Enumerable.Range(0, random.Next(0, 4)).Select(_ => ((ulong)random.NextInt64() << 1) | (ulong)(uint)random.Next(2)).ToArray()),
            12 => SecsItem.F4(Enumerable.Range(0, random.Next(0, 4)).Select(_ => BitConverter.Int32BitsToSingle(unchecked((int)random.NextInt64()))).ToArray()),
            _ => SecsItem.F8(Enumerable.Range(0, random.Next(0, 4)).Select(_ => BitConverter.Int64BitsToDouble(RandomSigned(random))).ToArray()),
        };
    }

    private static long RandomSigned(Random random) => unchecked(random.NextInt64() * (random.Next(2) == 0 ? 1 : -1));

    private static string RandomText(Random random) => new(Enumerable.Range(0, random.Next(0, 9)).Select(_ => (char)random.Next(256)).ToArray());

    private static byte[] RandomBytes(Random random) => Enumerable.Range(0, random.Next(0, 6)).Select(_ => (byte)random.Next(256)).ToArray();
}
