using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="BigEndianFields"/> 的测试：黄金向量、往返、读取的"字节不足不移动"约定、参数校验，以及连续写入一个报文头。
/// </summary>
public sealed class BigEndianFieldsTests
{
    [Fact]
    public void UInt8_0xAB_IsASingleByte()
    {
        Assert.Equal(new byte[] { 0xAB }, Written(1, output => BigEndianFields.WriteUInt8(output, 0xAB)));

        ReadOnlySpan<byte> data = new byte[] { 0xAB, 0xCD };
        Assert.True(BigEndianFields.TryTakeUInt8(ref data, out byte value));
        Assert.Equal((byte)0xAB, value);
        Assert.Equal(new byte[] { 0xCD }, data.ToArray());
    }

    [Fact]
    public void UInt16_0x1234_IsBigEndian()
    {
        Assert.Equal(new byte[] { 0x12, 0x34 }, Written(2, output => BigEndianFields.WriteUInt16(output, 0x1234)));

        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34 };
        Assert.True(BigEndianFields.TryTakeUInt16(ref data, out ushort value));
        Assert.Equal((ushort)0x1234, value);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void UInt24_0x123456_IsBigEndian()
    {
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, Written(3, output => BigEndianFields.WriteUInt24(output, 0x123456)));

        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34, 0x56 };
        Assert.True(BigEndianFields.TryTakeUInt24(ref data, out int value));
        Assert.Equal(0x123456, value);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void UInt32_0x12345678_IsBigEndian()
    {
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, Written(4, output => BigEndianFields.WriteUInt32(output, 0x12345678u)));

        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        Assert.True(BigEndianFields.TryTakeUInt32(ref data, out uint value));
        Assert.Equal(0x12345678u, value);
        Assert.True(data.IsEmpty);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    [InlineData(new byte[0])]
    public void WriteBytes_CopiesTheBytesVerbatim(byte[] value)
        => Assert.Equal(value, Written(value.Length, output => BigEndianFields.WriteBytes(output, value)));

    [Fact]
    public void RoundTrip_EveryWidthRestoresTheValue()
    {
        var writer = new ByteBufferWriter();
        BigEndianFields.WriteUInt8(writer, 0x12);
        BigEndianFields.WriteUInt16(writer, 0x3456);
        BigEndianFields.WriteUInt24(writer, 0x789ABC);
        BigEndianFields.WriteUInt32(writer, 0xDEF01234u);

        ReadOnlySpan<byte> data = writer.WrittenSpan;
        Assert.True(BigEndianFields.TryTakeUInt8(ref data, out byte b8));
        Assert.True(BigEndianFields.TryTakeUInt16(ref data, out ushort b16));
        Assert.True(BigEndianFields.TryTakeUInt24(ref data, out int b24));
        Assert.True(BigEndianFields.TryTakeUInt32(ref data, out uint b32));
        Assert.Equal((byte)0x12, b8);
        Assert.Equal((ushort)0x3456, b16);
        Assert.Equal(0x789ABC, b24);
        Assert.Equal(0xDEF01234u, b32);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void AProtocolHeader_IsWrittenFieldByFieldInOrder()
    {
        var writer = new ByteBufferWriter();
        BigEndianFields.WriteUInt32(writer, 0x00010000u);   // 4 字节长度
        BigEndianFields.WriteUInt16(writer, 0x0102);        // 2 字节命令码
        BigEndianFields.WriteUInt24(writer, 0x0A0B0C);      // 3 字节地址
        BigEndianFields.WriteUInt8(writer, 0x7F);           // 1 字节功能码
        BigEndianFields.WriteBytes(writer, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }); // 原样负载

        Assert.Equal(
            new byte[] { 0x00, 0x01, 0x00, 0x00, 0x01, 0x02, 0x0A, 0x0B, 0x0C, 0x7F, 0xDE, 0xAD, 0xBE, 0xEF },
            writer.WrittenSpan.ToArray());

        ReadOnlySpan<byte> data = writer.WrittenSpan;
        Assert.True(BigEndianFields.TryTakeUInt32(ref data, out uint length));
        Assert.True(BigEndianFields.TryTakeUInt16(ref data, out ushort command));
        Assert.True(BigEndianFields.TryTakeUInt24(ref data, out int address));
        Assert.True(BigEndianFields.TryTakeUInt8(ref data, out byte function));
        Assert.True(BigEndianFields.TryTakeBytes(ref data, 4, out ReadOnlySpan<byte> payload));
        Assert.Equal(0x00010000u, length);
        Assert.Equal((ushort)0x0102, command);
        Assert.Equal(0x0A0B0C, address);
        Assert.Equal((byte)0x7F, function);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, payload.ToArray());
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void TryTakeUInt8_WithExtraBytes_TakesOnlyTheLeadingByte()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x11, 0x22, 0x33 };
        Assert.True(BigEndianFields.TryTakeUInt8(ref data, out byte value));
        Assert.Equal((byte)0x11, value);
        Assert.Equal(new byte[] { 0x22, 0x33 }, data.ToArray());
    }

    [Fact]
    public void TryTakeUInt16_WithExtraBytes_LeavesTheRemainder()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        Assert.True(BigEndianFields.TryTakeUInt16(ref data, out ushort value));
        Assert.Equal((ushort)0x1234, value);
        Assert.Equal(new byte[] { 0x56, 0x78 }, data.ToArray());
    }

    [Fact]
    public void TryTakeUInt24_WithExtraBytes_LeavesTheRemainder()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        Assert.True(BigEndianFields.TryTakeUInt24(ref data, out int value));
        Assert.Equal(0x123456, value);
        Assert.Equal(new byte[] { 0x78 }, data.ToArray());
    }

    [Fact]
    public void TryTakeUInt32_WithExtraBytes_LeavesTheRemainder()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };
        Assert.True(BigEndianFields.TryTakeUInt32(ref data, out uint value));
        Assert.Equal(0x12345678u, value);
        Assert.Equal(new byte[] { 0x9A }, data.ToArray());
    }

    [Fact]
    public void TryTakeBytes_WithExtraBytes_LeavesTheRemainder()
    {
        ReadOnlySpan<byte> data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        Assert.True(BigEndianFields.TryTakeBytes(ref data, 2, out ReadOnlySpan<byte> value));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, value.ToArray());
        Assert.Equal(new byte[] { 0xCC, 0xDD }, data.ToArray());
    }

    [Fact]
    public void TryTakeBytes_ZeroLength_SucceedsWithAnEmptySlice()
    {
        ReadOnlySpan<byte> data = new byte[] { 0xAA, 0xBB };
        Assert.True(BigEndianFields.TryTakeBytes(ref data, 0, out ReadOnlySpan<byte> value));
        Assert.True(value.IsEmpty);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, data.ToArray());
    }

    [Fact]
    public void TryTakeBytes_ZeroLengthOnEmptyData_Succeeds()
    {
        ReadOnlySpan<byte> data = ReadOnlySpan<byte>.Empty;
        Assert.True(BigEndianFields.TryTakeBytes(ref data, 0, out ReadOnlySpan<byte> value));
        Assert.True(value.IsEmpty);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void TryTakeUInt8_WithNoBytes_ReturnsFalseWithoutMovingData()
    {
        ReadOnlySpan<byte> original = ReadOnlySpan<byte>.Empty;
        ReadOnlySpan<byte> data = original;
        Assert.False(BigEndianFields.TryTakeUInt8(ref data, out byte value));
        Assert.Equal((byte)0, value);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void TryTakeUInt16_WithOneByteShort_ReturnsFalseWithoutMovingData()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x12 };
        Assert.False(BigEndianFields.TryTakeUInt16(ref data, out ushort value));
        Assert.Equal((ushort)0, value);
        Assert.Equal(new byte[] { 0x12 }, data.ToArray());
    }

    [Fact]
    public void TryTakeUInt24_WithOneByteShort_ReturnsFalseWithoutMovingData()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34 };
        Assert.False(BigEndianFields.TryTakeUInt24(ref data, out int value));
        Assert.Equal(0, value);
        Assert.Equal(new byte[] { 0x12, 0x34 }, data.ToArray());
    }

    [Fact]
    public void TryTakeUInt32_WithOneByteShort_ReturnsFalseWithoutMovingData()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34, 0x56 };
        Assert.False(BigEndianFields.TryTakeUInt32(ref data, out uint value));
        Assert.Equal(0u, value);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, data.ToArray());
    }

    [Fact]
    public void TryTakeBytes_WithOneByteShort_ReturnsFalseWithoutMovingData()
    {
        ReadOnlySpan<byte> data = new byte[] { 0x12, 0x34 };
        Assert.False(BigEndianFields.TryTakeBytes(ref data, 3, out ReadOnlySpan<byte> value));
        Assert.True(value.IsEmpty);
        Assert.Equal(new byte[] { 0x12, 0x34 }, data.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xFFFFFF)]
    public void WriteUInt24_WithinRange_WritesThreeBytes(int value)
        => Assert.Equal(3, Written(3, output => BigEndianFields.WriteUInt24(output, value)).Length);

    [Theory]
    [InlineData(-1)]
    [InlineData(0x1000000)]
    public void WriteUInt24_OutOfRange_Throws(int value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Written(3, output => BigEndianFields.WriteUInt24(output, value)));

    [Fact]
    public void TryTakeBytes_NegativeLength_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TakeBytes(new byte[] { 0x12 }, -1));

    [Fact]
    public void WriteBytes_NullOutput_Throws()
        => Assert.Throws<ArgumentNullException>(() => BigEndianFields.WriteBytes(null!, new byte[] { 0x01 }));

    [Fact]
    public void WriteUInt8_NullOutput_Throws()
        => Assert.Throws<ArgumentNullException>(() => BigEndianFields.WriteUInt8(null!, 0x01));

    [Fact]
    public void WriteUInt16_NullOutput_Throws()
        => Assert.Throws<ArgumentNullException>(() => BigEndianFields.WriteUInt16(null!, 0x01));

    [Fact]
    public void WriteUInt24_NullOutput_Throws()
        => Assert.Throws<ArgumentNullException>(() => BigEndianFields.WriteUInt24(null!, 1));

    [Fact]
    public void WriteUInt32_NullOutput_Throws()
        => Assert.Throws<ArgumentNullException>(() => BigEndianFields.WriteUInt32(null!, 1u));

    [Fact]
    public void Widths_MatchTheDocumentedConstants()
    {
        Assert.Equal(1, BigEndianFields.UInt8Bytes);
        Assert.Equal(2, BigEndianFields.UInt16Bytes);
        Assert.Equal(3, BigEndianFields.UInt24Bytes);
        Assert.Equal(4, BigEndianFields.UInt32Bytes);
    }

    private static byte[] Written(int minimumCapacity, Action<System.Buffers.IBufferWriter<byte>> write)
    {
        var writer = new ByteBufferWriter(Math.Max(1, minimumCapacity));
        write(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static bool TakeBytes(byte[] data, int length)
    {
        ReadOnlySpan<byte> span = data;
        return BigEndianFields.TryTakeBytes(ref span, length, out _);
    }
}