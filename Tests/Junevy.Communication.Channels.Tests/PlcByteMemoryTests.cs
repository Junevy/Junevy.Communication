using Junevy.Communication.Testing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 共享库 <see cref="PlcByteMemory"/> 的契约（计划 Task 3、P46-D5/D6）：按（区名，块号）组织的字节存储，
/// 越界与"块不存在"由共享库判定，设备替身只保留协议翻译。
/// </summary>
/// <remarks>
/// 这些用例不使用真实套接字，因此不属于 <c>SocketTiming</c> 集合，可以并行运行。</remarks>
public sealed class PlcByteMemoryTests
{
    [Fact]
    public void Define_ReturnsAFreshZeroedBlock()
    {
        var memory = new PlcByteMemory();

        byte[] block = memory.Define("DataBlock", 1, 8);

        Assert.Equal(8, block.Length);
        Assert.All(block, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Define_ThenPreset_SeesThePresetValues()
    {
        var memory = new PlcByteMemory();
        byte[] block = memory.Define("M", 0, 4);

        block[0] = 0xAB;
        block[3] = 0xCD;

        Assert.Equal(0xAB, memory.Block("M", 0)[0]);
        Assert.Equal(0xCD, memory.Block("M", 0)[3]);
    }

    [Fact]
    public void Define_TheSameBlockTwice_Throws()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 4);

        Assert.Throws<InvalidOperationException>(() => memory.Define("D", 1, 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Define_NonPositiveLength_Throws(int length)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PlcByteMemory().Define("D", 1, length));

    [Fact]
    public void Block_UnknownBlock_Throws()
        => Assert.Throws<KeyNotFoundException>(() => new PlcByteMemory().Block("D", 1));

    [Fact]
    public void BlocksWithTheSameNumberInDifferentAreas_AreIndependent()
    {
        var memory = new PlcByteMemory();
        memory.Define("A", 1, 2)[0] = 1;
        memory.Define("B", 1, 2)[0] = 2;

        Assert.Equal(1, memory.Block("A", 1)[0]);
        Assert.Equal(2, memory.Block("B", 1)[0]);
    }

    [Fact]
    public void AreaNamesAreCaseSensitive()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 2);

        Assert.Throws<KeyNotFoundException>(() => memory.Block("d", 1));
    }

    [Fact]
    public void TryRead_ReadsTheRequestedWindow()
    {
        var memory = new PlcByteMemory();
        byte[] block = memory.Define("D", 1, 8);
        block[2] = 0x11;
        block[3] = 0x22;
        block[4] = 0x33;

        PlcByteAccess access = memory.TryRead("D", 1, 2, 3, out byte[] data);

        Assert.Equal(PlcByteAccess.Ok, access);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, data);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33 }, memory.Block("D", 1).Skip(2).Take(3));
    }

    [Fact]
    public void TryRead_ExactlyToTheEnd_Succeeds()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 4);

        Assert.Equal(PlcByteAccess.Ok, memory.TryRead("D", 1, 2, 2, out byte[] data));
        Assert.Equal(2, data.Length);
    }

    [Fact]
    public void TryRead_OneBytePastTheEnd_IsBeyondEnd()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 4);

        PlcByteAccess access = memory.TryRead("D", 1, 2, 3, out byte[] data);

        Assert.Equal(PlcByteAccess.BeyondEnd, access);
        Assert.Empty(data);
    }

    [Fact]
    public void TryRead_UnknownBlock_IsNoSuchBlock()
    {
        var memory = new PlcByteMemory();

        Assert.Equal(PlcByteAccess.NoSuchBlock, memory.TryRead("D", 9, 0, 1, out _));
    }

    [Fact]
    public void TryRead_NegativeOffset_Throws()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => memory.TryRead("D", 1, -1, 1, out _));
    }

    [Fact]
    public void TryRead_NegativeCount_Throws()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => memory.TryRead("D", 1, 0, -1, out _));
    }

    [Fact]
    public void TryWrite_StoresAtTheOffset()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 6);

        PlcByteAccess access = memory.TryWrite("D", 1, 2, new byte[] { 0xAA, 0xBB });

        Assert.Equal(PlcByteAccess.Ok, access);
        Assert.Equal(new byte[] { 0, 0, 0xAA, 0xBB, 0, 0 }, memory.Block("D", 1));
    }

    [Fact]
    public void TryWrite_OneBytePastTheEnd_LeavesTheBlockUntouched()
    {
        var memory = new PlcByteMemory();
        byte[] block = memory.Define("D", 1, 4);
        byte[] before = (byte[])block.Clone();

        PlcByteAccess access = memory.TryWrite("D", 1, 3, new byte[] { 0xFF, 0xFF });

        Assert.Equal(PlcByteAccess.BeyondEnd, access);
        Assert.Equal(before, memory.Block("D", 1));
    }

    [Fact]
    public void TryWrite_ExactlyToTheEnd_Succeeds()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 4);

        Assert.Equal(PlcByteAccess.Ok, memory.TryWrite("D", 1, 2, new byte[] { 1, 2 }));
        Assert.Equal(new byte[] { 0, 0, 1, 2 }, memory.Block("D", 1));
    }

    [Fact]
    public void TryWrite_UnknownBlock_IsNoSuchBlock()
        => Assert.Equal(PlcByteAccess.NoSuchBlock, new PlcByteMemory().TryWrite("D", 9, 0, new byte[] { 1 }));

    [Fact]
    public void TryWrite_NegativeOffset_Throws()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => memory.TryWrite("D", 1, -1, new byte[] { 1 }));
    }

    [Fact]
    public void ReadWriteRoundTrip_KeepsEveryByte()
    {
        var memory = new PlcByteMemory();
        var payload = new byte[16];
        new Random(11).NextBytes(payload);
        memory.Define("D", 1, 16);

        Assert.Equal(PlcByteAccess.Ok, memory.TryWrite("D", 1, 0, payload));
        Assert.Equal(PlcByteAccess.Ok, memory.TryRead("D", 1, 0, 16, out byte[] read));
        Assert.Equal(payload, read);
    }

    [Fact]
    public void ConcurrentWritersToDifferentOffsets_LeaveTheBlockComplete()
    {
        // 端到端用例里替身在服务端线程上写、用例在测试线程上读写，因此存储必须是线程安全的。
        const int writers = 8;
        const int stride = 64;
        var memory = new PlcByteMemory();
        memory.Define("D", 1, writers * stride);

        Parallel.For(0, writers, w => memory.TryWrite("D", 1, w * stride, Fill((byte)(w + 1), stride)));

        byte[] block = memory.Block("D", 1);
        for (int w = 0; w < writers; w++)
            Assert.Equal(Fill((byte)(w + 1), stride), block.Skip(w * stride).Take(stride));
    }

    [Fact]
    public void ConcurrentReadsWhileWriting_NeverThrow()
    {
        var memory = new PlcByteMemory();
        memory.Define("D", 1, 128);

        Parallel.For(0, 200, i =>
        {
            memory.TryWrite("D", 1, i % 100, new byte[] { 1 });
            memory.TryRead("D", 1, i % 100, 4, out _);
        });
    }

    private static byte[] Fill(byte value, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = value;
        return result;
    }
}