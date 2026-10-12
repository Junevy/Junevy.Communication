using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 内存区写（计划 Task 12 第 4、5 条）：0102 请求的数量按单位计（字写按字、位写按点，每点 1 字节 0x00 或 0x01），
/// 字与位共用同一段参数写入代码（第 18 节自查项）；写应答没有数据，结束码按 P4-D14 屏蔽。
/// </summary>
public sealed class FinsWriteTests
{
    [Fact]
    public void V_F_5_WriteD200_OneWord_QuantityCountsWords()
    {
        FinsAddress address = Address("D200");

        byte[] frame = FinsMemoryArea.BuildWriteRequest(Route, 0x02, address.Device.WordAreaCode, address.Number, ToBytes("00 FF"));

        Assert.Equal(V_F_5_Request, ToText(frame));
    }

    [Fact]
    public void Write_TwoWords_QuantityIsWordsNotBytes()
    {
        FinsAddress address = Address("D200");

        byte[] frame = FinsMemoryArea.BuildWriteRequest(Route, 0x02, address.Device.WordAreaCode, address.Number, ToBytes("00 FF 12 34"));

        Assert.Equal(Write_2Words, ToText(frame));
    }

    [Fact]
    public void V_F_7_WriteW10_03_OnePoint_CarriesOneByteOfData()
    {
        FinsAddress address = Address("W10.03");

        byte[] frame = FinsMemoryArea.BuildWriteBitsRequest(Route, 0x04, address.Device.BitAreaCode, address.Number, address.BitIndex ?? 0, new[] { true });

        Assert.Equal(V_F_7, ToText(frame));
    }

    [Fact]
    public void ThreePoints_AreOneByteEachAndKeepTheirOrder()
        => Assert.Equal(WriteBits_3Points, ToText(FinsMemoryArea.BuildWriteBitsRequest(Route, 0x04, WBitArea, 10, 3, new[] { true, false, true })));

    [Fact]
    public void AFalsePoint_IsEncodedAsZeroByte()
        => Assert.Equal(WriteBit_Zero, ToText(FinsMemoryArea.BuildWriteBitsRequest(Route, 0x04, WBitArea, 10, 3, new[] { false })));

    [Fact]
    public void WordAndBitWrites_ShareTheParameterLayout()
    {
        byte[] word = FinsMemoryArea.BuildWriteRequest(Route, 0x02, DWordArea, 200, ToBytes("00 FF"));
        byte[] bit = FinsMemoryArea.BuildWriteBitsRequest(Route, 0x04, WBitArea, 10, 3, new[] { true });

        // 17.2 节说明段：区域 1 + 字地址 2 + 位号 1 + 数量 2，两种写入都从命令码回显之后同一个位置开始。
        Assert.Equal("82 00 C8 00 00 01", ToText(word.AsSpan(AreaCodeIndex, FinsMemoryArea.ParametersBytes)));
        Assert.Equal("31 00 0A 03 00 01", ToText(bit.AsSpan(AreaCodeIndex, FinsMemoryArea.ParametersBytes)));
    }

    [Fact]
    public void V_F_5_Response_IsAcceptedWithoutData()
        => Succeeded(FinsMemoryArea.CheckWriteResponse(ToBytes(V_F_5_Response), 0x02, null));

    [Fact]
    public void WriteAck_WithData_IsAProtocolViolation()
        => AssertViolation(FinsMemoryArea.CheckWriteResponse(ToBytes(Write_Ack_UnexpectedData), 0x02, null));

    [Fact]
    public void ReadCommandEcho_IsNotAWriteAck()
        => AssertViolation(FinsMemoryArea.CheckWriteResponse(ToBytes(V_F_9), 0x06, null));

    [Fact]
    public void EndCode1103_IsARemoteError()
        => AssertRemoteError(FinsMemoryArea.CheckWriteResponse(WithEndCode(V_F_5_Response, 0x11, 0x03), 0x02, null), 0x1103, EndCodeMessage(0x1103));

    [Fact]
    public void StatusFlag_IsMaskedAwayAndLoggedAsDebug()
    {
        var logger = new TestLogger();

        Succeeded(FinsMemoryArea.CheckWriteResponse(WithEndCode(V_F_5_Response, 0x00, 0x40), 0x02, logger));

        // P4-D14：写应答的结束码同样屏蔽，原码含标志位时记 Debug。
        TestLogEntry entry = Assert.Single(logger.GetEntries(LogLevel.Debug));
        Assert.Contains("0x0040", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderError_IsARemoteErrorWithThatCode()
        => AssertRemoteError(FinsMemoryArea.CheckWriteResponse(WithTcpError(V_F_5_Response, 0xFE01), 0x02, null), 0xFE01, TcpErrorMessage(0xFE01));

    [Fact]
    public void SidMismatch_IsAProtocolViolation()
        => AssertViolation(FinsMemoryArea.CheckWriteResponse(Mutated(V_F_5_Response, SidIndex, 0x03), 0x02, null));

    [Fact]
    public void CommandEchoMismatch_IsAProtocolViolation()
        => AssertViolation(FinsMemoryArea.CheckWriteResponse(Mutated(V_F_5_Response, CommandIndex, 0x02), 0x02, null));

    [Fact]
    public void ResponseBitMissing_IsAProtocolViolation()
        => AssertViolation(FinsMemoryArea.CheckWriteResponse(Mutated(V_F_5_Response, IcfIndex, 0x80), 0x02, null));

    [Theory]
    [InlineData(ShellBytes - 1)]
    [InlineData(ShellBytes + FinsHead.HeadBytes)]
    [InlineData(ShellBytes + FinsHead.HeadBytes + 2)]
    public void TruncatedFrames_AreProtocolViolations(int length)
        => AssertViolation(FinsMemoryArea.CheckWriteResponse(Truncated(V_F_5_Response, length), 0x02, null));
}
