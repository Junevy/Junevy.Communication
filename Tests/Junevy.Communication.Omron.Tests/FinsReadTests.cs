using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 内存区读（计划 Task 12 第 4、5 条）：0101 请求用 17.3 节的字区或位区代码，参数是"区域 + 字地址 + 位号 + 数量"；
/// 应答解码按 Task 12 第 5 条依次检查 FINS/TCP 命令与错误码、ICF 应答位、SID 与命令码回显、结束码屏蔽（P4-D14），最后交出数据。
/// 区域代码取自解析后的软元件，因此这些向量同时核对第 17.3 节码表与 Task 13 字钩子要用的实参组合。
/// </summary>
public sealed class FinsReadTests
{
    [Fact]
    public void V_F_3_ReadD100_TwoWords_UsesTheWordAreaCodeAndZeroBitNumber()
    {
        FinsAddress address = Address("D100");

        byte[] frame = FinsMemoryArea.BuildReadRequest(Route, 0x01, address.Device.WordAreaCode, address.Number, 0, 2);

        Assert.Equal(V_F_3, ToText(frame));
    }

    [Fact]
    public void V_F_6_ReadCio000_ThreePoints_UsesTheBitAreaCodeAndBitNumber()
    {
        FinsAddress address = Address("CIO0.00");

        // Task 13 第 3 条：FINS 的位读取由套件改为按字读；位读请求只是同一个编码方法的另一组实参。
        byte[] frame = FinsMemoryArea.BuildReadRequest(Route, 0x03, address.Device.BitAreaCode, address.Number, address.BitIndex ?? 0, 3);

        Assert.Equal(V_F_6_Request, ToText(frame));
    }

    [Fact]
    public void EmGroupRead_UsesTheGroupWordAreaCode()
    {
        // P4-D20 ④：第 17.2 节没有 EM 的向量，这里按 17.3 码表核对区域代码（EM 第 2 组 = 字 0xA2）。
        FinsAddress address = Address("E2_500");

        byte[] frame = FinsMemoryArea.BuildReadRequest(Route, 0x07, address.Device.WordAreaCode, address.Number, 0, 1);

        Assert.Equal((byte)0xA2, frame[AreaCodeIndex]);
        Assert.Equal("01 01", ToText(frame.AsSpan(CommandIndex, 2)));
    }

    [Fact]
    public void V_F_4_ReadAck_ReturnsTheWordData()
    {
        byte[] data = Succeeded(FinsMemoryArea.ReadResponse(ToBytes(V_F_4), 0x01, 4, null));

        Assert.Equal("12 34 56 78", ToText(data));
    }

    [Fact]
    public void V_F_6_ReadAck_ReturnsOneBytePerPoint()
    {
        byte[] data = Succeeded(FinsMemoryArea.ReadResponse(ToBytes(V_F_6_Response), 0x03, 3, null));

        Assert.Equal("01 00 01", ToText(data));
    }

    [Fact]
    public void V_F_8_EndCode1103_IsARemoteError()
        => AssertRemoteError(FinsMemoryArea.ReadResponse(ToBytes(V_F_8), 0x05, 2, null), 0x1103, EndCodeMessage(0x1103));

    [Theory]
    [InlineData(0x00, 0x40)]
    [InlineData(0x80, 0x00)]
    [InlineData(0x80, 0x40)]
    [InlineData(0x80, 0x80)]
    public void P4_D14_StatusFlags_AreMaskedAwayAndStaySuccessful(int major, int minor)
    {
        // 17.3 节：主码 & 0x7F、子码 & 0x3F，结果为 0 即成功。两位标志都在掩码之外时命令仍然成功。
        byte[] data = Succeeded(FinsMemoryArea.ReadResponse(WithEndCode(V_F_9, major, minor), 0x06, 2, null));

        Assert.Equal("00 2A", ToText(data));
    }

    [Theory]
    [InlineData(0x11, 0x03, 0x1103)]
    [InlineData(0x91, 0x03, 0x1103)]
    [InlineData(0x00, 0x41, 0x0001)]
    public void P4_D14_RemainingEndCode_IsARemoteErrorWithTheMaskedCode(int major, int minor, int expected)
    {
        // 结束码在数据长度之前判断，所以这里沿用 V-F-8（无数据）来问 2 字节。
        var code = (ushort)expected;
        AssertRemoteError(FinsMemoryArea.ReadResponse(WithEndCode(V_F_8, major, minor), 0x05, 2, null), code, EndCodeMessage(code));
    }

    [Fact]
    public void V_F_9_FlaggedSuccess_LogsDebugWithTheRawEndCode()
    {
        var logger = new TestLogger();

        Succeeded(FinsMemoryArea.ReadResponse(ToBytes(V_F_9), 0x06, 2, logger));

        // P4-D14：原码含标志位时记 Debug。
        TestLogEntry entry = Assert.Single(logger.GetEntries(LogLevel.Debug));
        Assert.Contains("0x0040", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessWithoutFlags_LogsNothing()
    {
        var logger = new TestLogger();

        Succeeded(FinsMemoryArea.ReadResponse(ToBytes(V_F_4), 0x01, 4, logger));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void HeaderError_IsARemoteErrorWithThatCode()
        => AssertRemoteError(FinsMemoryArea.ReadResponse(WithTcpError(V_F_4, 0xFE01), 0x01, 4, null), 0xFE01, TcpErrorMessage(0xFE01));

    [Theory]
    [InlineData(V_F_4, 3)]
    [InlineData(V_F_4, 5)]
    [InlineData(V_F_9, 1)]
    public void PayloadNotTheRequestedLength_IsAProtocolViolation(string vector, int payloadBytes)
        => AssertViolation(FinsMemoryArea.ReadResponse(ToBytes(vector), Sid(vector), payloadBytes, null));

    [Fact]
    public void SidMismatch_IsAProtocolViolation()
        => AssertViolation(FinsMemoryArea.ReadResponse(Mutated(V_F_4, SidIndex, 0x02), 0x01, 4, null));

    [Fact]
    public void ResponseBitMissing_IsAProtocolViolation()
        => AssertViolation(FinsMemoryArea.ReadResponse(Mutated(V_F_4, IcfIndex, 0x80), 0x01, 4, null));

    [Theory]
    [InlineData(V_F_4, CommandIndex, 0x02)]
    [InlineData(V_F_4, CommandIndex + 1, 0x02)]
    public void CommandEchoMismatch_IsAProtocolViolation(string vector, int index, int value)
        => AssertViolation(FinsMemoryArea.ReadResponse(Mutated(vector, index, (byte)value), Sid(vector), 4, null));

    [Fact]
    public void NodeAddressResponse_IsNotAFinsFrame()
        => AssertViolation(FinsMemoryArea.ReadResponse(ToBytes(V_F_2), 0x01, 4, null));

    [Theory]
    [InlineData(ShellBytes - 1)]
    [InlineData(ShellBytes + FinsHead.HeadBytes - 1)]
    [InlineData(ShellBytes + FinsHead.HeadBytes + 2)]
    public void TruncatedFrames_AreProtocolViolations(int length)
        => AssertViolation(FinsMemoryArea.ReadResponse(Truncated(V_F_4, length), 0x01, 4, null));

    // SID 取自向量自身，避免把回显校验写成"永远匹配"。
    private static byte Sid(string vector) => ToBytes(vector)[SidIndex];
}
