using Junevy.Communication.DebugAssistant.Common;

namespace Junevy.Communication.DebugAssistant.Tests.Common;

/// <summary>校验的已知向量（计划 Task 4 第 3 条）。</summary>
public sealed class ChecksumTests
{
    [Fact]
    public void Crc16Modbus_OfAReadRequest_IsLowByteFirst()
    {
        // 01 03 00 00 00 01 的 CRC 为 0x0A84，线上低字节在前：84 0A。
        byte[] payload = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01];

        Assert.Equal(new byte[] { 0x84, 0x0A }, Checksums.Compute(ChecksumKind.Crc16Modbus, payload));
    }

    [Fact]
    public void Crc16CcittFalse_OfTheCheckString_IsHighByteFirst()
    {
        // ASCII "123456789" 的 CRC-16/CCITT-FALSE 为 0x29B1，高字节在前。
        byte[] payload = "123456789"u8.ToArray();

        Assert.Equal(new byte[] { 0x29, 0xB1 }, Checksums.Compute(ChecksumKind.Crc16CcittFalse, payload));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 }, 0x06)]
    [InlineData(new byte[] { 0xFF, 0x02 }, 0x01)]
    [InlineData(new byte[] { 0x10, 0x20, 0x30, 0x40 }, 0xA0)]
    public void Sum8_IsTheSumModulo256(byte[] payload, byte expected)
    {
        Assert.Equal(new[] { expected }, Checksums.Compute(ChecksumKind.Sum8, payload));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 }, 0x00)]
    [InlineData(new byte[] { 0xAA, 0x55 }, 0xFF)]
    [InlineData(new byte[] { 0x12, 0x34, 0x56 }, 0x70)]
    public void Xor8_IsTheExclusiveOrOfAllBytes(byte[] payload, byte expected)
    {
        Assert.Equal(new[] { expected }, Checksums.Compute(ChecksumKind.Xor8, payload));
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 }, 0xFA)]
    [InlineData(new byte[] { 0xFF, 0x02 }, 0xFF)]
    [InlineData(new byte[] { 0x10 }, 0xF0)]
    public void Lrc_IsTheTwosComplementOfTheSum(byte[] payload, byte expected)
    {
        Assert.Equal(new[] { expected }, Checksums.Compute(ChecksumKind.Lrc, payload));
    }

    [Fact]
    public void Append_AddsTheChecksumAfterThePayload_AndNoneAddsNothing()
    {
        byte[] payload = [0x01, 0x02, 0x03];

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x06 }, Checksums.Append(ChecksumKind.Sum8, payload));
        Assert.Equal(payload, Checksums.Append(ChecksumKind.None, payload));
    }
}
