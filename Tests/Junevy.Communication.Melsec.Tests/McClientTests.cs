using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec.Protocol;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// <see cref="McClient"/> 的工厂与选项（计划 Task 11）：传输与帧的组合校验、传输配置不被静默覆盖、选项取快照，
/// 点数上限（按帧与编码），以及末端软元件编号的范围检查（经公开的读取方法，在通道未连接之前返回）。
/// </summary>
public sealed class McClientTests
{
    [Fact(Timeout = 20000)]
    public void CreateTcp_With4CFrame_IsAnArgumentException()
    {
        Assert.Throws<ArgumentException>(() => { McClient.CreateTcp(new TcpClientChannelConfig(), new McClientOptions { FrameType = McFrameType.Frame4C }); });
    }

    [Fact(Timeout = 20000)]
    public void CreateUdp_With4CFrame_IsAnArgumentException()
    {
        Assert.Throws<ArgumentException>(() => { McClient.CreateUdp(new UdpChannelConfig(), new McClientOptions { FrameType = McFrameType.Frame4C }); });
    }

    [Fact(Timeout = 20000)]
    public void CreateSerial_With3EFrame_IsAnArgumentException()
    {
        Assert.Throws<ArgumentException>(() => { McClient.CreateSerial(new SerialChannelConfig(), new McClientOptions { FrameType = McFrameType.Frame3E }); });
    }

    [Fact(Timeout = 20000)]
    public void CreateTcp_WithFramingSetOnTheTransport_IsAnArgumentException()
    {
        var transport = new TcpClientChannelConfig { Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } } };

        Assert.Throws<ArgumentException>(() => { McClient.CreateTcp(transport, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void CreateTcp_WithCorrelationSetOnTheTransport_IsAnArgumentException()
    {
        var transport = new TcpClientChannelConfig { Correlation = CorrelationMode.Keyed };

        Assert.Throws<ArgumentException>(() => { McClient.CreateTcp(transport, new McClientOptions()); });
    }

    [Fact(Timeout = 20000)]
    public void CreateSerial_WithDefaultTransport_CreatesAClientWithoutOpeningThePort()
    {
        using McClient client = McClient.CreateSerial(new SerialChannelConfig(), new McClientOptions { FrameType = McFrameType.Frame4C });

        Assert.False(client.IsConnected);
    }

    [Fact(Timeout = 20000)]
    public void CreateTcp_LeavesTheCallersOptionsAndTransportUntouched()
    {
        var options = new McClientOptions { FrameType = McFrameType.Frame4E, MonitoringTimer = 7 };
        var transport = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5000 };

        using McClient client = McClient.CreateTcp(transport, options);

        Assert.False(client.IsConnected);
        Assert.Equal((ushort)7, options.MonitoringTimer);
        Assert.Equal(FramingMode.Raw, transport.Framing.Mode);
        Assert.Equal(CorrelationMode.Sequential, transport.Correlation);
    }

    [Fact(Timeout = 20000)]
    public void Options_AreSnapshottedAtConstruction_LaterChangesDoNotAffectTheSettings()
    {
        var options = new McClientOptions { MonitoringTimer = 7 };
        McProtocolSettings settings = McProtocolSettings.From(options);

        options.MonitoringTimer = 9;

        Assert.Equal((ushort)7, settings.MonitoringTimer);
    }

    [Fact(Timeout = 20000)]
    public void Options_RejectAZeroMonitoringTimer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => McProtocolSettings.From(new McClientOptions { MonitoringTimer = 0 }));
    }

    [Fact(Timeout = 20000)]
    public void Options_RejectAnUndefinedFrameType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => McProtocolSettings.From(new McClientOptions { FrameType = (McFrameType)99 }));
    }

    [Theory(Timeout = 20000)]
    [InlineData(McFrameType.Frame3E, McEncoding.Binary, 7168)]
    [InlineData(McFrameType.Frame3E, McEncoding.Ascii, 3584)]
    [InlineData(McFrameType.Frame4E, McEncoding.Binary, 7168)]
    [InlineData(McFrameType.Frame4E, McEncoding.Ascii, 3584)]
    [InlineData(McFrameType.Frame4C, McEncoding.Binary, 7904)]
    [InlineData(McFrameType.Frame4C, McEncoding.Ascii, 7904)]
    public void MaxBitPoints_FollowsTheFrameAndTheEncoding(McFrameType frameType, McEncoding encoding, int expected)
    {
        Assert.Equal(expected, McProtocolSettings.From(new McClientOptions { FrameType = frameType, Encoding = encoding }).MaxBitPoints);
    }

    [Fact(Timeout = 20000)]
    public async Task AsciiDecimalNumber_BeyondSixDigits_IsRejectedBeforeTheChannelIsUsed()
    {
        using McClient client = UnconnectedClient(McEncoding.Ascii);

        CommResult<byte[]> result = await client.ReadAsync("D1000000", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task BinaryDecimalNumber_UpToThreeBytes_PassesTheRangeCheck()
    {
        using McClient client = UnconnectedClient(McEncoding.Binary);

        CommResult<byte[]> result = await client.ReadAsync("D1000000", 1);

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task BitRead_OnAWordDevice_PassesValidationAndIsServedByWordReads()
    {
        // 字软元件的位读取由 Plc 层改为读取覆盖这些点的字（P2-D10），因此它通过范围检查，并在未连接时返回 NotConnected。
        using McClient client = UnconnectedClient(McEncoding.Binary);

        CommResult<bool[]> result = await client.ReadBitsAsync("D100", 1);

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task WordAccess_OnABitDevice_CoversSixteenPointsPerWord()
    {
        // M999980 的一个字覆盖 999980 至 999995，在十进制 6 位的上限之内；M999990 的一个字覆盖到 1000005，超出。
        using McClient client = UnconnectedClient(McEncoding.Ascii);

        Assert.Equal(CommErrorKind.NotConnected, (await client.ReadAsync("M999980", 1)).ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.ReadAsync("M999990", 1)).ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task UnknownDevice_IsInvalidRequest()
    {
        using McClient client = UnconnectedClient(McEncoding.Binary);

        CommResult<byte[]> result = await client.ReadAsync("Q10", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    private static McClient UnconnectedClient(McEncoding encoding)
        => new McClient(new DuplexClientChannel(new ClientChannelSettings()), new McClientOptions { Encoding = encoding });
}
