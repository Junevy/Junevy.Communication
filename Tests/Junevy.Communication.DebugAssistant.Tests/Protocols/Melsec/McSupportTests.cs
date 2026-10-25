using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Melsec;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Melsec;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Melsec;

/// <summary>
/// MC 的支持类型（计划 Task 9 第 1、2 条）：帧类型、编码与传输的全排列（18 种），软元件速查表与 <c>ParseAddress</c> 的一致性（28 项），
/// 访问路径的十六进制输入，以及监视定时器的毫秒换算与警告。
/// </summary>
public sealed class McSupportTests
{
    /// <summary>计划 7.3 节的合法组合：TCP 与 UDP 用 3E、4E，串口只用 4C。这里单独写出，不借用被测代码。</summary>
    private static bool IsLegal(PlcTransport transport, McFrameType frame) => transport == PlcTransport.Serial
        ? frame == McFrameType.Frame4C
        : frame != McFrameType.Frame4C;

    public static IEnumerable<object[]> AllCombinations =>
        from transport in Enum.GetValues<PlcTransport>()
        from frame in Enum.GetValues<McFrameType>()
        from encoding in Enum.GetValues<McEncoding>()
        select new object[] { transport, frame, encoding, IsLegal(transport, frame) };

    public static IEnumerable<object[]> Devices => McDevices.All.Select(device => new object[] { device });

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void EveryCombination_IsSelectableOnlyWhenThePlanAllowsIt(PlcTransport transport, McFrameType frame, McEncoding encoding, bool legal)
    {
        Assert.Equal(legal, McCombination.Problem(transport, frame) is null);
        Assert.Equal(legal, McCombination.Name(transport, frame, encoding) is not null);
        Assert.Equal(legal, McCombination.FramesFor(transport).Contains(frame));
    }

    [Theory]
    [InlineData(PlcTransport.Tcp, McFrameType.Frame3E, McEncoding.Binary, "3E（二进制，TCP）")]
    [InlineData(PlcTransport.Tcp, McFrameType.Frame4E, McEncoding.Ascii, "4E（ASCII，TCP）")]
    [InlineData(PlcTransport.Udp, McFrameType.Frame3E, McEncoding.Ascii, "3E（ASCII，UDP）")]
    [InlineData(PlcTransport.Udp, McFrameType.Frame4E, McEncoding.Binary, "4E（二进制，UDP）")]
    [InlineData(PlcTransport.Serial, McFrameType.Frame4C, McEncoding.Ascii, "4C 格式 4（ASCII，串口）")]
    [InlineData(PlcTransport.Serial, McFrameType.Frame4C, McEncoding.Binary, "4C 格式 5（二进制，串口）")]
    public void LegalCombination_IsNamedWithItsFormatAndTransport(PlcTransport transport, McFrameType frame, McEncoding encoding, string expected) =>
        Assert.Equal(expected, McCombination.Name(transport, frame, encoding));

    [Theory]
    [InlineData(PlcTransport.Serial, McFrameType.Frame3E, "4C")]
    [InlineData(PlcTransport.Serial, McFrameType.Frame4E, "4C")]
    [InlineData(PlcTransport.Tcp, McFrameType.Frame4C, "3E")]
    [InlineData(PlcTransport.Udp, McFrameType.Frame4C, "3E")]
    public void IllegalCombination_ExplainsWhichFramesTheTransportTakes(PlcTransport transport, McFrameType frame, string expected) =>
        Assert.Contains(expected, McCombination.Problem(transport, frame) ?? string.Empty);

    [Fact]
    public void DeviceTable_HasTwentyEightDevices_WithUniqueSymbols()
    {
        Assert.Equal(28, McDevices.All.Count);
        Assert.Equal(28, McDevices.All.Select(device => device.Symbol).Distinct().Count());
    }

    [Fact]
    public void DeviceTable_HighlightsTheNineHexadecimalDevices()
    {
        string[] hexadecimal = McDevices.All.Where(device => device.Radix == 16).Select(device => device.Symbol).ToArray();

        Assert.Equal(new[] { "X", "Y", "B", "W", "SB", "SW", "DX", "DY", "ZR" }, hexadecimal);
    }

    [Theory]
    [MemberData(nameof(Devices))]
    public void DeviceTable_AgreesWithParseAddress(McDeviceEntry device)
    {
        using McClient client = McClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5001 }, new ScriptedTransports().Tcp, new McClientOptions());

        CommResult<PlcAddress> parsed = client.ParseAddress(McDevices.SampleAddress(device));

        Assert.True(parsed.IsSuccess, parsed.ErrorMessage);
        Assert.Equal((device.Symbol, device.Unit, device.Radix), (parsed.Data!.Device.Symbol, parsed.Data.Device.Unit, parsed.Data.Device.Radix));
    }

    [Theory]
    [InlineData("0x1F", 0x1F)]
    [InlineData("1f", 0x1F)]
    [InlineData(" FF ", 0xFF)]
    [InlineData("00", 0x00)]
    public void AccessPathByte_ReadsHexWithOrWithoutThePrefix(string text, byte expected) =>
        Assert.Equal(expected, McAccessPath.ParseByte(text));

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("G1")]
    [InlineData("100")]
    [InlineData("FFFFFFFF")]
    public void AccessPathByte_RejectsValuesOutsideOneByte(string text) =>
        Assert.Throws<FormatException>(() => McAccessPath.ParseByte(text));

    [Theory]
    [InlineData("03FF", 0x03FF)]
    [InlineData("0xFFFF", 0xFFFF)]
    [InlineData("0", 0x0000)]
    public void AccessPathWord_ReadsHexWithOrWithoutThePrefix(string text, ushort expected) =>
        Assert.Equal(expected, McAccessPath.ParseWord(text));

    [Theory]
    [InlineData("")]
    [InlineData("10000")]
    [InlineData("FFFFFFFF")]
    [InlineData("0x1G")]
    public void AccessPathWord_RejectsValuesOutsideOneWord(string text) =>
        Assert.Throws<FormatException>(() => McAccessPath.ParseWord(text));

    [Theory]
    [InlineData(4, 2000, 1000L, false)]
    [InlineData(3, 1000, 750L, false)]
    [InlineData(1, 250, 250L, true)]
    [InlineData(7, 1750, 1750L, true)]
    [InlineData(65535, 2000, 16383750L, true)]
    public void MonitoringTimer_ConvertsTo250MsUnits_AndWarnsAtOrAboveTheRequestTimeout(int units, int requestTimeout, long milliseconds, bool warns)
    {
        (long actual, string? warning) = McTimers.Describe(units, requestTimeout);

        Assert.Equal(milliseconds, actual);
        Assert.Equal(warns, warning is not null);
    }
}
