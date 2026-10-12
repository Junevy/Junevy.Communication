using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 客户端入口与选项（计划 Task 9 第 2、4 条、P4-D10）：S7 只有 S7overTCP，内置创建器表里只有一条 TCP，
/// 数据报交付的自定义创建器直接拒绝；选项在校验之后才创建通道，构造失败时释放已经创建的通道。
/// 本期不提供"接收现成通道"的公开构造函数，因此 <see cref="S7Client"/> 的构造函数只在替身里使用。
/// </summary>
public sealed class S7ClientTests
{
    [Fact]
    public void Create_OverTcp_CreatesAnUnconnectedClientWithoutNegotiatedPdu()
    {
        using S7Client client = S7Client.Create(Transport(), new S7ClientOptions());

        Assert.False(client.IsConnected);
        Assert.Null(client.NegotiatedPduSize);
    }

    [Fact]
    public void Create_OverUdp_IsNotSupported()
    {
        // P4-D2：S7 只走 TCP，内置创建器表里没有 UDP 的位置。
        Assert.Throws<NotSupportedException>(() => S7Client.Create(new UdpChannelConfig(), new S7ClientOptions()));
    }

    [Fact]
    public void Create_OverADatagramCreator_IsAnArgumentExceptionBeforeAnyChannelIsCreated()
    {
        // P4-D10：S7 需要字节流才能按 TPKT 分帧，数据报交付的创建器不接受。
        var creator = new RecordingChannelCreator(typeof(TcpClientChannelConfig), ChannelDelivery.Datagram, ChannelMedium.Network);

        Assert.Throws<ArgumentException>(() => S7Client.Create(Transport(), creator, new S7ClientOptions()));

        Assert.Equal(0, creator.CreateCount);
    }

    [Fact]
    public void Create_WithANullTransport_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => S7Client.Create(null!, new S7ClientOptions()));
    }

    [Fact]
    public void CreateOverCreator_WithANullTransport_IsAnArgumentNullException()
    {
        var creator = new RecordingChannelCreator(typeof(TcpClientChannelConfig), ChannelDelivery.Stream, ChannelMedium.Network);

        Assert.Throws<ArgumentNullException>(() => S7Client.Create(null!, creator, new S7ClientOptions()));
    }

    [Fact]
    public void Create_WithANullCreator_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => S7Client.Create(Transport(), null!, new S7ClientOptions()));
    }

    [Fact]
    public void Create_WithNullOptions_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => S7Client.Create(Transport(), (S7ClientOptions)null!));
    }

    [Theory]
    [InlineData(8, 0, 1)]
    [InlineData(0, 32, 1)]
    [InlineData(0, 0, 99)]
    public void Create_WithOptionsOutsideP4_D19_IsAnArgumentOutOfRangeException(int rack, int slot, int connectionType)
    {
        var options = new S7ClientOptions { Rack = rack, Slot = slot, ConnectionType = (S7ConnectionType)connectionType };

        Assert.Throws<ArgumentOutOfRangeException>(() => S7Client.Create(Transport(), options));
    }

    [Fact]
    public async Task Create_WhenTheClientConstructionFails_DisposesTheChannelItCreated()
    {
        // RetryCount 为负：PlcClientBase 在通道创建之后才校验选项，因此构造失败时通道必须已被释放。
        var creator = new RecordingChannelCreator(typeof(TcpClientChannelConfig), ChannelDelivery.Stream, ChannelMedium.Network);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => S7Client.Create(Transport(), creator, new S7ClientOptions { RetryCount = -1 }));

        Assert.Equal("RetryCount", exception.ParamName);
        Assert.NotNull(creator.CreatedChannel);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => creator.CreatedChannel!.ConnectAsync());
    }

    [Fact]
    public void Create_LeavesTheCallersOptionsAndTransportUntouched()
    {
        var options = new S7ClientOptions { Rack = 2, Slot = 3, ConnectionType = S7ConnectionType.OP };
        var transport = LoopbackTcp.Client(5000);

        using S7Client client = S7Client.Create(transport, options);

        Assert.Equal(2, options.Rack);
        Assert.Equal(3, options.Slot);
        Assert.Equal(S7ConnectionType.OP, options.ConnectionType);
        Assert.Null(options.DataFormat);
        Assert.Equal(FramingMode.Raw, transport.Framing.Mode);
        Assert.Equal(CorrelationMode.Sequential, transport.Correlation);
    }

    [Fact]
    public void DataFormat_DefaultsToBigEndianWordOrder()
    {
        using S7Client client = S7Client.Create(Transport(), new S7ClientOptions());

        // P4-D11：S7 的设备内存顺序就是规范化布局，默认字序 ABCD、字符串 ASCII。
        Assert.Equal(WordOrder.ABCD, client.DataFormat.WordOrder);
        Assert.Equal(Encoding.ASCII, client.DataFormat.StringEncoding);
    }

    [Fact]
    public void DataFormat_FromTheOptions_IsCarriedIntoTheClient()
    {
        var format = new PlcDataFormat(WordOrder.BADC, Encoding.UTF8);

        using S7Client client = S7Client.Create(Transport(), new S7ClientOptions { DataFormat = format });

        Assert.Same(format, client.DataFormat);
    }

    [Fact]
    public void Settings_SnapshotTheOptionsAndApplyTheDefaultFormat()
    {
        var options = new S7ClientOptions { Rack = 2, Slot = 3, ConnectionType = S7ConnectionType.Basic };
        S7ProtocolSettings settings = S7ProtocolSettings.From(options);

        options.Rack = 7;

        Assert.Equal((int)S7ConnectionType.Basic, settings.ConnectionType);
        Assert.Equal(2, settings.Rack);
        Assert.Equal(3, settings.Slot);
        Assert.Equal(WordOrder.ABCD, settings.DataFormat.WordOrder);
        Assert.Equal(Encoding.ASCII, settings.DataFormat.StringEncoding);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 0)]
    [InlineData(7, 31)]
    public void Settings_AcceptEveryValueInsideP4_D19(int rack, int slot)
    {
        S7ProtocolSettings settings = S7ProtocolSettings.From(new S7ClientOptions { Rack = rack, Slot = slot });

        Assert.Equal(rack, settings.Rack);
        Assert.Equal(slot, settings.Slot);
    }

    [Fact]
    public async Task Operations_WhileDisconnected_FailAsNotConnected()
    {
        using S7Client client = S7Client.Create(Transport(), new S7ClientOptions());

        CommResult<byte[]> read = await client.ReadBytesAsync("M10", 1);

        Assert.Equal(CommErrorKind.NotConnected, read.ErrorKind);
    }

    [Fact]
    public async Task Operation_AfterDispose_FailsAsNotConnected()
    {
        var client = S7Client.Create(Transport(), new S7ClientOptions());
        client.Dispose();

        CommResult read = await client.WriteBytesAsync("M10", new byte[] { 1 });

        Assert.Equal(CommErrorKind.NotConnected, read.ErrorKind);
    }

    [Fact]
    public async Task Operation_WithoutTheHandshake_InSession_FailsAsUnspecified()
    {
        // P4-D12：分包上限取自会话快照，钩子只在握手之后调用；没有快照值是接线错误，异常由流水线收口为 Unspecified。
        var channel = new DuplexClientChannel(new ClientChannelSettings());
        var client = new S7Client(channel, S7ProtocolSettings.From(new S7ClientOptions()), new S7ClientOptions(), new S7Session(), null);
        await using var owned = client;
        Succeeded(await client.ConnectAsync());

        CommResult<byte[]> result = await client.ReadBytesAsync("M10", 1);

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
    }

    // 端口为 0 的 TCP 配置在创建通道时就被 TcpClientChannel 拒绝，因此统一用 S7 的标准端口 102（不连接，只是让通道构造成功）。
    private static TcpClientChannelConfig Transport() => LoopbackTcp.Client(102);
}
