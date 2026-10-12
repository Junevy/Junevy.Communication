using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 客户端入口与选项（计划 Task 13 第 5 条、P4-D10、P4-D11、P4-D18）：FINS 只有 FINSTCP，内置创建器表里只有一条 TCP，
/// 数据报交付的自定义创建器直接拒绝；选项先校验再创建通道，构造失败时释放已经创建的通道。
/// 本期不提供"接收现成通道"的公开构造函数，因此 <see cref="FinsClient"/> 的构造函数只在替身里使用。
/// </summary>
public sealed class FinsClientTests
{
    [Fact]
    public void Create_OverTcp_CreatesAnUnconnectedClientWithoutNodeNumbers()
    {
        using FinsClient client = FinsClient.Create(Transport(), new FinsClientOptions());

        Assert.False(client.IsConnected);
        Assert.Null(client.ClientNode);
        Assert.Null(client.ServerNode);
    }

    [Fact]
    public void Create_OverUdp_IsNotSupported()
    {
        // P4-D2：FINS 只走 TCP，内置创建器表里没有 UDP 的位置。
        Assert.Throws<NotSupportedException>(() => FinsClient.Create(new UdpChannelConfig(), new FinsClientOptions()));
    }

    [Fact]
    public void Create_OverADatagramCreator_IsAnArgumentExceptionBeforeAnyChannelIsCreated()
    {
        // P4-D10：FINS 需要字节流才能按长度字段分帧，数据报交付的创建器不接受。
        var creator = new RecordingChannelCreator(typeof(TcpClientChannelConfig), ChannelDelivery.Datagram, ChannelMedium.Network);

        Assert.Throws<ArgumentException>(() => FinsClient.Create(Transport(), creator, new FinsClientOptions()));

        Assert.Equal(0, creator.CreateCount);
    }

    [Fact]
    public void Create_WithANullTransport_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FinsClient.Create(null!, new FinsClientOptions()));
    }

    [Fact]
    public void CreateOverCreator_WithANullTransport_IsAnArgumentNullException()
    {
        var creator = new RecordingChannelCreator(typeof(TcpClientChannelConfig), ChannelDelivery.Stream, ChannelMedium.Network);

        Assert.Throws<ArgumentNullException>(() => FinsClient.Create(null!, creator, new FinsClientOptions()));
    }

    [Fact]
    public void Create_WithANullCreator_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FinsClient.Create(Transport(), null!, new FinsClientOptions()));
    }

    [Fact]
    public void Create_WithNullOptions_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => FinsClient.Create(Transport(), (FinsClientOptions)null!));
    }

    [Fact]
    public void Create_WithTheBroadcastClientNode_IsRejectedBeforeAnyChannelIsCreated()
    {
        // P4-D18：255 是 FINS 的广播地址；连接参数在通道创建之前校验，因此替身一次也没被调用。
        var creator = new RecordingChannelCreator(typeof(TcpClientChannelConfig), ChannelDelivery.Stream, ChannelMedium.Network);

        Assert.Throws<ArgumentOutOfRangeException>(() => FinsClient.Create(Transport(), creator, new FinsClientOptions { ClientNode = 255 }));

        Assert.Equal(0, creator.CreateCount);
    }

    [Fact]
    public async Task Create_WhenTheClientConstructionFails_DisposesTheChannelItCreated()
    {
        // RetryCount 为负：PlcClientBase 在通道创建之后才校验选项，因此构造失败时通道必须已被释放。
        var creator = new RecordingChannelCreator(typeof(TcpClientChannelConfig), ChannelDelivery.Stream, ChannelMedium.Network);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => FinsClient.Create(Transport(), creator, new FinsClientOptions { RetryCount = -1 }));

        Assert.Equal("RetryCount", exception.ParamName);
        Assert.NotNull(creator.CreatedChannel);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => creator.CreatedChannel!.ConnectAsync());
    }

    [Fact]
    public void Create_LeavesTheCallersOptionsAndTransportUntouched()
    {
        var options = new FinsClientOptions { ClientNode = 11, DestinationNetwork = 2, DestinationUnit = 3 };
        var transport = LoopbackTcp.Client(9600);

        using FinsClient client = FinsClient.Create(transport, options);

        Assert.Equal(11, options.ClientNode);
        Assert.Equal(2, options.DestinationNetwork);
        Assert.Equal(3, options.DestinationUnit);
        Assert.Null(options.DataFormat);
        Assert.Equal(FramingMode.Raw, transport.Framing.Mode);
        Assert.Equal(CorrelationMode.Sequential, transport.Correlation);
    }

    [Fact]
    public void DataFormat_DefaultsToCdabWordOrder()
    {
        using FinsClient client = FinsClient.Create(Transport(), new FinsClientOptions());

        // P4-D11：欧姆龙的 32 位数低字在前、字内大端，规范化布局即设备内存顺序。
        Assert.Equal(WordOrder.CDAB, client.DataFormat.WordOrder);
        Assert.Equal(Encoding.ASCII, client.DataFormat.StringEncoding);
    }

    [Fact]
    public void DataFormat_FromTheOptions_IsCarriedIntoTheClient()
    {
        var format = new PlcDataFormat(WordOrder.ABCD, Encoding.UTF8);

        using FinsClient client = FinsClient.Create(Transport(), new FinsClientOptions { DataFormat = format });

        Assert.Same(format, client.DataFormat);
    }

    [Fact]
    public void Settings_SnapshotTheOptionsAndApplyTheDefaultFormat()
    {
        var options = new FinsClientOptions { ClientNode = 11, DestinationNetwork = 2, DestinationUnit = 3 };
        FinsProtocolSettings settings = FinsProtocolSettings.From(options);

        options.ClientNode = 7;

        Assert.Equal(11, settings.ClientNode);
        Assert.Equal(2, settings.DestinationNetwork);
        Assert.Equal(3, settings.DestinationUnit);
        Assert.Equal(WordOrder.CDAB, settings.DataFormat.WordOrder);
        Assert.Equal(Encoding.ASCII, settings.DataFormat.StringEncoding);
    }

    [Fact]
    public async Task Operations_WhileDisconnected_FailAsNotConnected()
    {
        using FinsClient client = FinsClient.Create(Transport(), new FinsClientOptions());

        CommResult<byte[]> read = await client.ReadBytesAsync("D100", 1);

        Assert.Equal(CommErrorKind.NotConnected, read.ErrorKind);
    }

    [Fact]
    public async Task Operation_AfterDispose_FailsAsNotConnected()
    {
        var client = FinsClient.Create(Transport(), new FinsClientOptions());
        client.Dispose();

        CommResult read = await client.WriteBytesAsync("D100", new byte[] { 1, 2 });

        Assert.Equal(CommErrorKind.NotConnected, read.ErrorKind);
    }

    [Fact]
    public async Task Operation_WithoutTheNodeHandshake_FailsAsUnspecified()
    {
        // P4-D18：路由的节点号取自会话快照，钩子只在握手之后才有值可用；没有快照是接线错误，异常由流水线收口为 Unspecified。
        var channel = new DuplexClientChannel(new ClientChannelSettings());
        var client = new FinsClient(channel, FinsProtocolSettings.From(new FinsClientOptions()), new FinsClientOptions(), new FinsSession(), null);
        await using var owned = client;
        Succeeded(await client.ConnectAsync());

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
    }

    // 端口为 0 的 TCP 配置在创建通道时就被 TcpClientChannel 拒绝，因此统一用 FINS 的标准端口 9600（不连接，只是让通道构造成功）。
    private static TcpClientChannelConfig Transport() => LoopbackTcp.Client(9600);
}
