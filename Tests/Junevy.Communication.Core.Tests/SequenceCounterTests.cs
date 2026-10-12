using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="SequenceCounter"/> 的测试：起始值、递增、宽度回绕与并发唯一性。
/// </summary>
public sealed class SequenceCounterTests
{
    [Fact]
    public void NextUInt16_StartsAtOne()
        => Assert.Equal((ushort)1, new SequenceCounter().NextUInt16());

    [Fact]
    public void NextByte_StartsAtOne()
        => Assert.Equal((byte)1, new SequenceCounter().NextByte());

    [Fact]
    public void NextUInt16_IncrementsByOne()
    {
        var counter = new SequenceCounter();
        Assert.Equal((ushort)1, counter.NextUInt16());
        Assert.Equal((ushort)2, counter.NextUInt16());
        Assert.Equal((ushort)3, counter.NextUInt16());
    }

    [Fact]
    public void NextByte_IncrementsByOne()
    {
        var counter = new SequenceCounter();
        Assert.Equal((byte)1, counter.NextByte());
        Assert.Equal((byte)2, counter.NextByte());
        Assert.Equal((byte)3, counter.NextByte());
    }

    [Fact]
    public void NextUInt16_After65535_WrapsToZero()
    {
        var counter = new SequenceCounter();
        Assert.Equal((ushort)65535, Take(counter.NextUInt16, 65535));
        Assert.Equal((ushort)0, counter.NextUInt16());
        Assert.Equal((ushort)1, counter.NextUInt16());
    }

    [Fact]
    public void NextByte_After255_WrapsToZero()
    {
        var counter = new SequenceCounter();
        Assert.Equal((byte)255, Take(counter.NextByte, 255));
        Assert.Equal((byte)0, counter.NextByte());
        Assert.Equal((byte)1, counter.NextByte());
    }

    [Fact]
    public void NextUInt16_KeepsCountingAcrossTheWrap()
    {
        var counter = new SequenceCounter();
        Take(counter.NextUInt16, 65535);
        for (ushort expected = 0; expected < 5; expected++)
            Assert.Equal(expected, counter.NextUInt16());
    }

    [Fact]
    public void NextByte_KeepsCountingAcrossTheWrap()
    {
        var counter = new SequenceCounter();
        Take(counter.NextByte, 255);
        for (byte expected = 0; expected < 5; expected++)
            Assert.Equal(expected, counter.NextByte());
    }

    [Fact]
    public void NextUInt16_FromConcurrentCallers_NeverRepeatsAValue()
    {
        const int Callers = 8;
        const int PerCaller = 1000;

        var counter = new SequenceCounter();
        int[] values = TakeConcurrently(Callers, PerCaller, counter.NextUInt16);

        Assert.Equal(Callers * PerCaller, values.Distinct().Count());
        Assert.Equal((ushort)Callers * PerCaller, values.Max());
    }

    [Fact]
    public void NextByte_FromConcurrentCallers_NeverRepeatsAValue()
    {
        const int Callers = 8;
        const int PerCaller = 32;

        var counter = new SequenceCounter();
        int[] values = TakeConcurrently(Callers, PerCaller, counter.NextByte);

        // 256 次取值刚好绕回一次：1–255 各出现一次，256 截断成 0。
        Assert.Equal(Callers * PerCaller, values.Distinct().Count());
        Assert.Equal((byte)255, values.Max());
        Assert.Equal(1, values.Count(value => value == 0));
    }

    private static ushort Take(Func<ushort> next, int times)
    {
        for (int i = 1; i < times; i++)
            next();
        return next();
    }

    private static byte Take(Func<byte> next, int times)
    {
        for (int i = 1; i < times; i++)
            next();
        return next();
    }

    private static int[] TakeConcurrently<T>(int callers, int perCaller, Func<T> next)
        where T : struct
    {
        var collected = new int[callers * perCaller];
        Parallel.For(0, callers, caller =>
        {
            for (int i = 0; i < perCaller; i++)
                collected[(caller * perCaller) + i] = Convert.ToInt32(next());
        });

        return collected;
    }
}