using Junevy.Communication.Melsec.Protocol;
using static Junevy.Communication.Melsec.Tests.McTestSupport;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 字段编码规则（计划 Task 9）：位数据的半字节填充（手册 p.72 的五点示例）、ASCII 与二进制的软元件编号、编号与点数上限，以及 ASCII 位应答的校验。
/// </summary>
public sealed class McFieldEncodingTests
{
    [Fact(Timeout = 20000)]
    public void BitWrite_FivePointsBinary_PacksTwoPointsPerByteAndPadsTheLastNibble()
    {
        // 手册 p.72：M10 起 ON、OFF、ON、OFF、ON 的 E71 二进制为 10H 10H 10H（奇数点的最低半字节为 0）。
        byte[] body = BitWrite(McEncoding.Binary, "M10", new[] { true, false, true, false, true });

        Assert.Equal(Hex("01 14 01 00 0A 00 00 90 05 00 10 10 10"), body);
    }

    [Fact(Timeout = 20000)]
    public void BitWrite_FivePointsAscii_IsOneCharacterPerPoint()
    {
        // 手册 p.72：ASCII 为 "10101"（31H 30H 31H 30H 31H）。
        byte[] body = BitWrite(McEncoding.Ascii, "M10", new[] { true, false, true, false, true });

        Assert.Equal(Text("1401" + "0001" + "M*" + "000010" + "0005" + "10101"), body);
    }

    [Fact(Timeout = 20000)]
    public void BitRead_FivePointsBinary_TakesTheHighNibbleOfEachByteFirst()
    {
        McResponse response = Succeeded(Decode(McFrameType.Frame3E, McEncoding.Binary, 0,
            Hex("D0 00 00 FF FF 03 00 05 00 00 00 10 10 10"), McDataKind.Bits, 5));

        Assert.Equal(new byte[] { 1, 0, 1, 0, 1 }, response.Data);
    }

    [Fact(Timeout = 20000)]
    public void BitRead_FivePointsAscii_DecodesOneCharacterPerPoint()
    {
        McResponse response = Succeeded(Decode(McFrameType.Frame3E, McEncoding.Ascii, 0,
            Text("D000" + "00FF03FF00" + "0009" + "0000" + "10101"), McDataKind.Bits, 5));

        Assert.Equal(new byte[] { 1, 0, 1, 0, 1 }, response.Data);
    }

    [Fact(Timeout = 20000)]
    public void BitRead_Ascii_AnythingButZeroOrOne_IsAProtocolViolation()
    {
        AssertProtocolViolation(Decode(McFrameType.Frame3E, McEncoding.Ascii, 0,
            Text("D000" + "00FF03FF00" + "0009" + "0000" + "1010X"), McDataKind.Bits, 5));
    }

    [Fact(Timeout = 20000)]
    public void DeviceNumber_Ascii_IsWrittenInTheDeviceRadix()
    {
        // 手册 p.67：十进制软元件的编号按十进制数字书写，十六进制软元件按十六进制数字书写；数值 26 在两者中的文本不同。
        Assert.Equal(Text("0401" + "0000" + "M*" + "000026" + "0001"), WordRead(McEncoding.Ascii, "M26", 1));
        Assert.Equal(Text("0401" + "0000" + "X*" + "00001A" + "0001"), WordRead(McEncoding.Ascii, "X1A", 1));
    }

    [Fact(Timeout = 20000)]
    public void DeviceNumber_Binary_IsThreeBytesLittleEndianForBothRadixes()
    {
        Assert.Equal(Hex("01 04 00 00 1A 00 00 90 01 00"), WordRead(McEncoding.Binary, "M26", 1));
        Assert.Equal(Hex("01 04 00 00 1A 00 00 9C 01 00"), WordRead(McEncoding.Binary, "X1A", 1));
    }

    [Fact(Timeout = 20000)]
    public void DeviceNumber_AsciiHexadecimal_AcceptsTheSixDigitMaximum()
    {
        var (device, number) = Head("ZRFFFFFF");

        byte[] body = McRequestBody.BatchRead(McFieldCodec.For(McEncoding.Ascii), McCommands.WordUnit, device, number, 1);

        Assert.Equal(Text("0401" + "0000" + "ZR" + "FFFFFF" + "0001"), body);
    }

    [Fact(Timeout = 20000)]
    public void DeviceNumber_AsciiDecimal_AboveSixDigits_IsRejectedWhileBinaryAcceptsIt()
    {
        // ASCII 十进制编号为 6 位数字，最大 999999（手册 p.67）；二进制为 3 字节（1000000 = 0F4240H）。
        var (device, _) = Head("D1");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            McRequestBody.BatchRead(McFieldCodec.For(McEncoding.Ascii), McCommands.WordUnit, device, 1000000, 1);
        });
        Assert.Equal(Hex("01 04 00 00 40 42 0F A8 01 00"),
            McRequestBody.BatchRead(McFieldCodec.For(McEncoding.Binary), McCommands.WordUnit, device, 1000000, 1));
    }

    [Fact(Timeout = 20000)]
    public void PointLimits_MatchTheManualTables()
    {
        // 手册 p.70：字访问 960 字；以太网位访问，二进制 7168 点，ASCII 3584 点；串口位访问 7904 点。
        Assert.Equal(960, McLimits.WordPoints);
        Assert.Equal(7168, McLimits.EthernetBinaryBitPoints);
        Assert.Equal(3584, McLimits.EthernetAsciiBitPoints);
        Assert.Equal(7904, McLimits.SerialBitPoints);
    }
}
