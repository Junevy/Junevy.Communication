using Junevy.Communication.Secs;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// SECS-II 数据项的构造与访问器（P51-D3、D4，计划 Task 2）：每种格式的 <see cref="SecsItem.Format"/> 与
/// <see cref="SecsItem.Count"/>、访问器的往返、格式不符时的异常、以及工厂方法复制入参数组带来的不可变性。
/// </summary>
public sealed class SecsItemTests
{
    [Fact]
    public void List_KeepsTheChildrenAndCountsThem()
    {
        SecsItem item = SecsItem.L(SecsItem.A("EQ01"), SecsItem.U1(7));

        Assert.Equal(SecsFormat.List, item.Format);
        Assert.Equal(2, item.Count);
        Assert.Collection(
            item.Items,
            first => Assert.Equal("EQ01", first.GetString()),
            second => Assert.Equal(new byte[] { 7 }, second.GetU1()));
    }

    [Fact]
    public void EmptyList_HasNoChildren()
    {
        SecsItem item = SecsItem.L();

        Assert.Equal(SecsFormat.List, item.Format);
        Assert.Equal(0, item.Count);
        Assert.Empty(item.Items);
    }

    [Fact]
    public void NonListItems_HaveNoChildren()
    {
        Assert.Empty(SecsItem.U1(1).Items);
        Assert.Empty(SecsItem.A("A").Items);
        Assert.Empty(SecsItem.B(1).Items);
    }

    [Fact]
    public void Ascii_CountsCharacters()
    {
        Assert.Equal(5, SecsItem.A("EQ01\n").Count);
    }

    [Fact]
    public void U1_CountsElements()
    {
        Assert.Equal(3, SecsItem.U1(1, 2, 3).Count);
    }

    [Fact]
    public void Binary_CountsBytes()
    {
        Assert.Equal(2, SecsItem.B(0x80, 0x01).Count);
    }

    [Fact]
    public void U1AndBinary_ShareTheElementWidthButNotTheFormat()
    {
        // 两者都是单字节，但 SECS-II 的格式码不同，访问器也必须互不通用。
        Assert.Equal(SecsFormat.U1, SecsItem.U1(1).Format);
        Assert.Equal(SecsFormat.Binary, SecsItem.B(1).Format);
        Assert.Throws<InvalidOperationException>(() => SecsItem.U1(1).GetBinary());
        Assert.Throws<InvalidOperationException>(() => SecsItem.B(1).GetU1());
    }

    [Fact]
    public void SignedIntegers_RoundTripThroughTheirAccessors()
    {
        Assert.Equal(new sbyte[] { sbyte.MinValue, -1, 0, 1, sbyte.MaxValue },
            SecsItem.I1(sbyte.MinValue, -1, 0, 1, sbyte.MaxValue).GetI1());
        Assert.Equal(new short[] { short.MinValue, -1, 0, 1, short.MaxValue },
            SecsItem.I2(short.MinValue, -1, 0, 1, short.MaxValue).GetI2());
        Assert.Equal(new[] { int.MinValue, -1, 0, 1, int.MaxValue },
            SecsItem.I4(int.MinValue, -1, 0, 1, int.MaxValue).GetI4());
        Assert.Equal(new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue },
            SecsItem.I8(long.MinValue, -1L, 0L, 1L, long.MaxValue).GetI8());
    }

    [Fact]
    public void UnsignedIntegers_RoundTripThroughTheirAccessors()
    {
        Assert.Equal(new byte[] { 0, 1, byte.MaxValue }, SecsItem.U1(0, 1, byte.MaxValue).GetU1());
        Assert.Equal(new ushort[] { ushort.MinValue, 0, ushort.MaxValue }, SecsItem.U2(ushort.MinValue, 0, ushort.MaxValue).GetU2());
        Assert.Equal(new uint[] { uint.MinValue, 0, uint.MaxValue }, SecsItem.U4(uint.MinValue, 0, uint.MaxValue).GetU4());
        Assert.Equal(new[] { ulong.MinValue, 0UL, ulong.MaxValue }, SecsItem.U8(ulong.MinValue, 0UL, ulong.MaxValue).GetU8());
    }

    [Fact]
    public void Floats_RoundTripIncludingTheExtremes()
    {
        Assert.Equal(
            new[] { float.MinValue, -1.5f, 0f, 1.5f, float.MaxValue },
            SecsItem.F4(float.MinValue, -1.5f, 0f, 1.5f, float.MaxValue).GetF4());
        Assert.Equal(
            new[] { double.MinValue, -1.5, 0d, 1.5, double.MaxValue },
            SecsItem.F8(double.MinValue, -1.5, 0d, 1.5, double.MaxValue).GetF8());
    }

    [Fact]
    public void Booleans_RoundTrip()
    {
        Assert.Equal(new[] { true, false, true }, SecsItem.Boolean(true, false, true).GetBooleans());
    }

    [Fact]
    public void Binary_RoundTrips()
    {
        Assert.Equal(new byte[] { 0x00, 0x7F, 0x80, 0xFF }, SecsItem.B(0x00, 0x7F, 0x80, 0xFF).GetBinary());
    }

    [Fact]
    public void Ascii_RoundTripsEveryLatin1Character()
    {
        // P51-D4：单字节字符，U+0000–U+00FF 无损往返。
        string all = string.Concat(Enumerable.Range(0, 256).Select(code => (char)code));
        Assert.Equal(all, SecsItem.A(all).GetString());
    }

    [Fact]
    public void Ascii_RejectsCharactersOutsideLatin1()
    {
        // U+00E9 仍在 Latin-1 内，第一个越界的字符是 U+4E2D。
        ArgumentException error = Assert.Throws<ArgumentException>(() => SecsItem.A("EQ01\u00e9\u4e2d"));
        Assert.Contains("U+4E2D", error.Message);
        Assert.Contains("index 5", error.Message);
    }

    [Fact]
    public void Factories_CopyTheCallerArraySoTheItemIsImmutable()
    {
        var values = new uint[] { 1, 2, 3 };
        SecsItem item = SecsItem.U4(values);
        values[0] = 99;

        Assert.Equal(new uint[] { 1, 2, 3 }, item.GetU4());
    }

    [Fact]
    public void List_CopiesTheCallerArrayAndKeepsTheChildren()
    {
        var children = new[] { SecsItem.U1(1) };
        SecsItem item = SecsItem.L(children);
        children[0] = SecsItem.U1(2);

        Assert.Equal(new byte[] { 1 }, Assert.Single(item.Items).GetU1());
    }

    [Fact]
    public void List_RejectsNullChildren()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() => SecsItem.L(SecsItem.U1(1), null!));
        Assert.Equal("items", error.ParamName);
    }

    [Fact]
    public void Factories_RejectANullArray()
    {
        Assert.Throws<ArgumentNullException>(() => SecsItem.L(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.A(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.B(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.Boolean(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.I1(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.I2(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.I4(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.I8(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.U1(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.U2(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.U4(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.U8(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.F4(null!));
        Assert.Throws<ArgumentNullException>(() => SecsItem.F8(null!));
    }

    [Fact]
    public void Accessors_RejectTheWrongFormatAndNameIt()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetI4());
        Assert.Contains("Ascii", error.Message);

        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetBinary());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetBooleans());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetI1());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetI2());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetI8());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetU1());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetU2());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetU4());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetU8());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetF4());
        Assert.Throws<InvalidOperationException>(() => SecsItem.A("EQ01").GetF8());
    }

    [Fact]
    public void EmptyItems_RoundTripToEmptyLists()
    {
        Assert.Empty(SecsItem.Boolean().GetBooleans());
        Assert.Empty(SecsItem.B().GetBinary());
        Assert.Empty(SecsItem.U1().GetU1());
        Assert.Empty(SecsItem.U8().GetU8());
        Assert.Empty(SecsItem.F8().GetF8());
        Assert.Equal(string.Empty, SecsItem.A(string.Empty).GetString());
    }
}