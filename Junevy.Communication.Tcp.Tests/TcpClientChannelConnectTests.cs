using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp.Client;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 客户端通道的连接、本地绑定、保活与分帧的真实套接字测试（计划 10.3）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TcpClientChannelConnectTests
{
    private readonly ITestOutputHelper output;

    /// <summary>创建测试类（xUnit 注入输出辅助，用于记录实际观察到的耗时）。</summary>
    public TcpClientChannelConnectTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact(Timeout = 30000)]
    public async Task Connect_Success()
    {
        using var server = ScriptedTcpServer.Start((index, stream, token) => DrainAsync(stream, token));
        var config = CreateConfig(server.Port);
        await using var channel = new TcpClientChannel(config);

        CommResult result = await WithinAsync(channel.ConnectAsync(), 5000);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(ConnectionState.Connected, channel.State);
        Assert.True(channel.IsConnected);
        Assert.False(channel.IsTlsActive);
        Assert.Same(config, channel.Config);

        IPEndPoint? remote = channel.RemoteEndPoint;
        Assert.NotNull(remote);
        Assert.Equal(IPAddress.Loopback, remote!.Address);
        Assert.Equal(server.Port, remote.Port);
        Assert.NotNull(channel.LocalEndPoint);
    }

    [Fact(Timeout = 30000)]
    public async Task Connect_Refused_ReturnsConnectionClosed()
    {
        // 端口在监听释放之后即为未监听状态，连接被拒绝。
        var config = CreateConfig(FreePort());
        await using var channel = new TcpClientChannel(config);

        CommResult result = await WithinAsync(channel.ConnectAsync(), 10000);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Contains("ConnectionRefused", result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(channel.RemoteEndPoint);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
    }

    [BlackholeAddressFact(Timeout = 30000)]
    public async Task Connect_Timeout_ReturnsTimeoutWithinBudget()
    {
        // 10.255.255.1 为不可路由地址；ConnectTimeout 设为 500 毫秒，区间为 [400, 2500] 毫秒。
        var config = new TcpClientChannelConfig { Host = "10.255.255.1", Port = 9, ConnectTimeout = 500 };
        await using var channel = new TcpClientChannel(config);
        CommResult? result = null;

        TimeSpan elapsed = await TimingAssert.WithinAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(2500),
            async () => { result = await channel.ConnectAsync(); });

        output.WriteLine($"Unroutable address 10.255.255.1: result {result}; elapsed {elapsed.TotalMilliseconds:F0} ms.");
        Assert.NotNull(result);
        Assert.Equal(CommErrorKind.Timeout, result!.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task Connect_InvalidHost_ReturnsConnectionClosed()
    {
        var config = new TcpClientChannelConfig { Host = "nonexistent.invalid", Port = 80, ConnectTimeout = 10000 };
        await using var channel = new TcpClientChannel(config);
        var stopwatch = Stopwatch.StartNew();

        CommResult result = await WithinAsync(channel.ConnectAsync(), 15000);

        stopwatch.Stop();
        output.WriteLine($"Invalid host nonexistent.invalid: result {result}; elapsed {stopwatch.ElapsedMilliseconds} ms.");
        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
    }

    [Fact(Timeout = 30000)]
    public async Task LocalBinding_UsesLocalPort()
    {
        var remotePort = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = LoopbackServer.Start((index, client, token) =>
        {
            remotePort.TrySetResult(((IPEndPoint)client.Client.RemoteEndPoint!).Port);
            return Task.CompletedTask;
        });

        int localPort = FreePort();
        var config = CreateConfig(server.Port);
        config.LocalAddress = "127.0.0.1";
        config.LocalPort = localPort;
        await using var channel = new TcpClientChannel(config);

        CommResult result = await WithinAsync(channel.ConnectAsync(), 10000);

        Assert.True(result.IsSuccess, result.ToString());
        IPEndPoint? local = channel.LocalEndPoint;
        Assert.NotNull(local);
        Assert.Equal(localPort, local!.Port);
        Assert.Equal(localPort, await WithinAsync(remotePort.Task, 5000));
    }

    [Fact(Timeout = 30000)]
    public async Task KeepAlive_AppliedWithoutError()
    {
        using var server = ScriptedTcpServer.Start((index, stream, token) => DrainAsync(stream, token));
        var socketOptions = new TcpSocketOptions
        {
            KeepAlive = new TcpKeepAliveOptions { Enabled = true, Time = 30000, Interval = 5000, RetryCount = 3 },
        };

        // 连接器级：直接读回套接字上的保活选项。
        var connector = new TcpConnector("127.0.0.1", server.Port, null, 0, 10000, socketOptions, NullLogger.Instance);
        CommResult<Socket> connected = await WithinAsync(connector.ConnectAsync(CancellationToken.None), 10000);
        Assert.True(connected.IsSuccess, connected.ToString());

        using Socket socket = connected.Data!;
        Assert.Equal(1, ReadInt(socket, SocketOptionLevel.Socket, SocketOptionName.KeepAlive));
#if NET8_0_OR_GREATER
        // net8.0 读回按秒设置的值：30000 毫秒为 30 秒，5000 毫秒为 5 秒。net472 只能写入，不做读回断言。
        Assert.Equal(30, ReadInt(socket, SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime));
        Assert.Equal(5, ReadInt(socket, SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval));
        Assert.Equal(3, ReadInt(socket, SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount));
#endif

        // 通道级：带保活配置的连接可以建立。
        var config = CreateConfig(server.Port);
        config.Socket = socketOptions;
        await using var channel = new TcpClientChannel(config);
        CommResult result = await WithinAsync(channel.ConnectAsync(), 10000);
        Assert.True(result.IsSuccess, result.ToString());
    }

    [Fact(Timeout = 30000)]
    public async Task Framing_Delimiter_EndToEnd()
    {
        FramingOptions framing = LineFraming();
        IFrameCodecFactory codec = FrameCodecFactory.Create(framing);
        await using DeviceSimulatorTcpHost device = new DeviceSimulator(codec, frame => frame).HostTcp();
        var config = CreateConfig(device.Port);
        config.Framing = framing;
        await using var channel = new TcpClientChannel(config);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

        CommResult<byte[]> hello = await WithinAsync(channel.RequestAsync(Ascii("HELLO")), 5000);
        Assert.True(hello.IsSuccess, hello.ToString());
        Assert.Equal(Ascii("HELLO"), DataOf(hello));

        // 9000 字节跨越读取缓冲区，验证分隔符分帧在真实 TCP 上的重组。
        byte[] large = Enumerable.Repeat((byte)'x', 9000).ToArray();
        CommResult<byte[]> echoed = await WithinAsync(channel.RequestAsync(large), 5000);
        Assert.True(echoed.IsSuccess, echoed.ToString());
        Assert.Equal(large, DataOf(echoed));
    }

    [Fact(Timeout = 30000)]
    public async Task Framing_LengthField_EndToEnd()
    {
        var framing = new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = 0,
            LengthFieldSize = 2,
            LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
        };
        IFrameCodecFactory codec = FrameCodecFactory.Create(framing);
        await using DeviceSimulatorTcpHost device = new DeviceSimulator(codec, frame => frame).HostTcp();
        var config = CreateConfig(device.Port);
        config.Framing = framing;
        await using var channel = new TcpClientChannel(config);
        Assert.True((await WithinAsync(channel.ConnectAsync(), 10000)).IsSuccess);

        // 长度字段分帧的编码器原样发送，因此请求必须是完整的线路帧（含长度头）；设备原样回显。
        byte[] small = WireFrame(Ascii("PING-PAYLOAD"));
        CommResult<byte[]> first = await WithinAsync(channel.RequestAsync(small), 5000);
        Assert.True(first.IsSuccess, first.ToString());
        Assert.Equal(small, DataOf(first));

        byte[] large = WireFrame(Enumerable.Repeat((byte)'L', 5000).ToArray());
        CommResult<byte[]> second = await WithinAsync(channel.RequestAsync(large), 5000);
        Assert.True(second.IsSuccess, second.ToString());
        Assert.Equal(large, DataOf(second));
    }

    [Fact(Timeout = 30000)]
    public async Task Config_ChangedAfterConstruction_IsIgnored()
    {
        using var server = ScriptedTcpServer.Start((index, stream, token) => DrainAsync(stream, token));
        var config = CreateConfig(server.Port);
        await using var channel = new TcpClientChannel(config);

        // 构造之后修改配置对象：通道按构造时的快照连接原来的地址。
        config.Host = "nonexistent.invalid";
        config.Port = FreePort();
        config.ConnectTimeout = 1;

        CommResult result = await WithinAsync(channel.ConnectAsync(), 10000);

        Assert.True(result.IsSuccess, result.ToString());
        IPEndPoint? remote = channel.RemoteEndPoint;
        Assert.NotNull(remote);
        Assert.Equal(server.Port, remote!.Port);
    }

    // 读回整数型套接字选项（GetSocketOption 返回 object）。
    private static int ReadInt(Socket socket, SocketOptionLevel level, SocketOptionName name)
        => Convert.ToInt32(socket.GetSocketOption(level, name), CultureInfo.InvariantCulture);

    // 长度字段帧：2 字节大端长度头 + 负载。
    private static byte[] WireFrame(byte[] body)
    {
        var frame = new byte[body.Length + 2];
        frame[0] = (byte)(body.Length >> 8);
        frame[1] = (byte)body.Length;
        Buffer.BlockCopy(body, 0, frame, 2, body.Length);
        return frame;
    }
}
