using System.Net;
using System.Net.Sockets;
using System.Text;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 共享测试套件（Junevy.Communication.Testing）的自检测试：内存双工流、模拟设备、UDP/TCP 脚本服务端、耗时断言与日志收集。
/// 整个类位于 SocketTiming 集合中，套接字用例因此串行执行。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TestingKitTests
{
    [Fact]
    public async Task DuplexStream_RoundTrip()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        var payload = new byte[1024 * 1024];
        new Random(5).NextBytes(payload);
        var received = new byte[payload.Length];

        Task readTask = ReadFullAsync(pair.B, received);
        Task writeTask = pair.A.WriteAsync(payload, 0, payload.Length);

        await AwaitWithinAsync(Task.WhenAll(writeTask, readTask), 10000);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task DuplexStream_Abort_FailsPendingRead()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        Task<int> pendingRead = pair.B.ReadAsync(new byte[16], 0, 16);
        await Task.Delay(100);

        pair.Abort();

        Task completed = await Task.WhenAny(pendingRead, Task.Delay(1000));
        Assert.Same(pendingRead, completed);
        await Assert.ThrowsAsync<IOException>(() => pendingRead);
    }

    [Fact]
    public async Task DuplexStream_PauseThreshold_BlocksWriter()
    {
        using DuplexStreamPair pair = DuplexStreamPair.Create(pauseWriterThreshold: 4096);
        var payload = new byte[64 * 1024];
        Task writeTask = pair.A.WriteAsync(payload, 0, payload.Length);

        await Task.Delay(500);
        Assert.False(writeTask.IsCompleted, "The writer must stay blocked while the peer does not read.");

        var received = new byte[payload.Length];
        await AwaitWithinAsync(ReadFullAsync(pair.B, received), 5000);
        await AwaitWithinAsync(writeTask, 5000);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task DeviceSimulator_EchoesOverDuplex()
    {
        IFrameCodecFactory codec = FrameCodecFactory.Create(new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } });
        using DuplexStreamPair pair = DuplexStreamPair.Create();
        using var cancellation = new CancellationTokenSource();
        var simulator = new DeviceSimulator(codec, frame => frame);
        Task device = simulator.RunAsync(pair.B, cancellation.Token);

        var writer = new ByteArrayBufferWriter();
        IFrameEncoder encoder = codec.CreateEncoder();
        encoder.Encode(Ascii("PING"), writer);
        encoder.Encode(Ascii("PONG2"), writer);
        byte[] request = writer.ToArray();
        await AwaitWithinAsync(pair.A.WriteAsync(request, 0, request.Length), 5000);

        byte[] echoed = new byte[request.Length];
        await AwaitWithinAsync(ReadFullAsync(pair.A, echoed), 5000);
        Assert.Equal("PING\r\nPONG2\r\n", Encoding.ASCII.GetString(echoed));

        cancellation.Cancel();
        await AwaitWithinAsync(device, 5000);
    }

    [Fact]
    public async Task DeviceSimulatorTcpHost_EchoesOverTcp()
    {
        IFrameCodecFactory codec = FrameCodecFactory.Create(new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } });
        var simulator = new DeviceSimulator(codec, frame => frame);
        await using DeviceSimulatorTcpHost host = simulator.HostTcp();

        using var client = new TcpClient();
        await AwaitWithinAsync(client.ConnectAsync(IPAddress.Loopback, host.Port), 5000);

        NetworkStream stream = client.GetStream();
        byte[] request = Ascii("PING\r\n");
        await AwaitWithinAsync(stream.WriteAsync(request, 0, request.Length), 5000);

        byte[] echoed = new byte[request.Length];
        await AwaitWithinAsync(ReadFullAsync(stream, echoed), 5000);
        Assert.Equal("PING\r\n", Encoding.ASCII.GetString(echoed));
        Assert.Equal(1, host.AcceptedConnectionCount);
    }

    [Fact]
    public async Task ScriptedUdpPeer_RepliesAndCounts()
    {
        using var peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(index == 0 ? new byte[] { 0xAA } : null));
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await AwaitWithinAsync(client.SendAsync(new byte[] { 1 }, 1, peer.EndPoint), 3000);
        await AwaitWithinAsync(client.SendAsync(new byte[] { 2 }, 1, peer.EndPoint), 3000);

        UdpReceiveResult reply = await AwaitWithinAsync(client.ReceiveAsync(), 3000);
        Assert.Equal(new byte[] { 0xAA }, reply.Buffer);

        await WaitUntilAsync(() => peer.ReceivedCount == 2, 3000);
        Assert.Equal(2, peer.ReceivedCount);

        // 第二个数据报没有回复：之后 300 ms 内不应收到任何数据报。
        Task<UdpReceiveResult> second = client.ReceiveAsync();
        Task finished = await Task.WhenAny(second, Task.Delay(300));
        Assert.NotSame(second, finished);
    }

    [Fact]
    public async Task ScriptedTcpServer_CountsReceivedBytes_AndPeerClose()
    {
        using var server = ScriptedTcpServer.Start(async (index, stream, cancellationToken) =>
        {
            var buffer = new byte[256];
            while (await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken) > 0)
            {
            }
        });

        using var client = new TcpClient();
        await AwaitWithinAsync(client.ConnectAsync(IPAddress.Loopback, server.Port), 5000);
        byte[] data = { 1, 2, 3, 4 };
        await AwaitWithinAsync(client.GetStream().WriteAsync(data, 0, data.Length), 5000);

        await WaitUntilAsync(() => server.ReceivedBytes == 4, 3000);
        client.Close();

        await WaitUntilAsync(() => server.ClosedByPeerCount == 1, 3000);
        Assert.Equal(1, server.AcceptedConnectionCount);
    }

    [Fact]
    public async Task ScriptedTcpServer_CloseConnection_ClosesSelectedConnection()
    {
        using var server = ScriptedTcpServer.Start(async (index, stream, cancellationToken) =>
        {
            var buffer = new byte[16];
            while (await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken) > 0)
            {
            }
        });

        using var client = new TcpClient();
        await AwaitWithinAsync(client.ConnectAsync(IPAddress.Loopback, server.Port), 5000);
        await WaitUntilAsync(() => server.AcceptedConnectionCount == 1, 3000);

        server.CloseConnection(0);

        // 服务端关闭连接后，客户端读到流末尾。
        var buffer = new byte[1];
        int read = await AwaitWithinAsync(client.GetStream().ReadAsync(buffer, 0, buffer.Length), 3000);
        Assert.Equal(0, read);
        Assert.Throws<ArgumentOutOfRangeException>(() => server.CloseConnection(1));
    }

    [Fact]
    public async Task SilentTcpServer_CountsReceivedBytes_AndPeerClose()
    {
        using var server = SilentTcpServer.Start();
        using var client = new TcpClient();
        await AwaitWithinAsync(client.ConnectAsync(IPAddress.Loopback, server.Port), 5000);

        byte[] data = new byte[10];
        await AwaitWithinAsync(client.GetStream().WriteAsync(data, 0, data.Length), 5000);
        await WaitUntilAsync(() => server.ReceivedBytes == 10, 3000);

        client.Close();
        await WaitUntilAsync(() => server.ClosedByPeerCount == 1, 3000);
    }

    [Fact]
    public void TestLogger_RecordsLevelCategoryAndException()
    {
        var logger = new TestLogger();
        ILogger categorized = logger.CreateLogger("Junevy.Test");

        categorized.LogWarning("Connection {Id} lost", 7);
        logger.LogInformation("started");
        logger.LogError(new InvalidOperationException("boom"), "failed");

        IReadOnlyList<TestLogEntry> warnings = logger.GetEntries(LogLevel.Warning);
        Assert.Single(warnings);
        Assert.Equal("Junevy.Test", warnings[0].CategoryName);
        Assert.Equal("Connection 7 lost", warnings[0].Message);
        Assert.Equal(3, logger.Entries.Count);
        Assert.IsType<InvalidOperationException>(logger.GetEntries(LogLevel.Error)[0].Exception);
    }

    [Fact]
    public async Task TimingAssert_WithinRange_ReturnsElapsed()
    {
        TimeSpan elapsed = await TimingAssert.WithinAsync(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(2080), () => Task.Delay(100));
        Assert.InRange(elapsed.TotalMilliseconds, 80d, 2080d);
    }

    [Fact]
    public async Task TimingAssert_OutsideRange_Throws()
    {
        await Assert.ThrowsAsync<TimingOutOfRangeException>(
            () => TimingAssert.WithinAsync(TimeSpan.Zero, TimeSpan.FromMilliseconds(1), () => Task.Delay(50)));
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static async Task ReadFullAsync(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, total, buffer.Length - total);
            if (read == 0)
                throw new EndOfStreamException("The stream ended before the expected number of bytes arrived.");

            total += read;
        }
    }

    private static async Task AwaitWithinAsync(Task task, int milliseconds)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(milliseconds));
        Assert.Same(task, completed);
        await task;
    }

    private static async Task<T> AwaitWithinAsync<T>(Task<T> task, int milliseconds)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(milliseconds));
        Assert.Same(task, completed);
        return await task;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int milliseconds)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition(), "The condition was not met within the time limit.");
    }
}
