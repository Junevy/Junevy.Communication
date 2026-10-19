using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>
/// 模拟器宿主在 TCP、UDP 与串口上的收发（计划 Task 11 第 2 条）：引擎用测试替身，故障注入在线路上生效，请求日志按请求触发。
/// 串口测试只使用环境变量给出的虚拟串口对（<c>JUNEVY_SERIAL_PAIR</c>）。
/// </summary>
public sealed class SimulatorHostTests
{
    [Fact]
    public async Task Tcp_RepliesToEachRequest_AndRaisesTheRequestLog()
    {
        int port = FreePort.Get();
        await using var host = new SimulatorHost(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port }, new EchoEngine(), new FaultInjector());
        var log = new ConcurrentQueue<SimulatorRequestEventArgs>();
        host.RequestHandled += (_, e) => log.Enqueue(e);
        Assert.True((await host.StartAsync()).IsSuccess);

        using var client = new TcpClient("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 0xAA, 1, 2, 3 }, await ReadAsync(stream, 2000));
        SimulatorRequestEventArgs entry = Assert.Single(log);
        Assert.Equal((SimulatorReplyKind.Reply, "回显 3 字节"), (entry.Kind, entry.Summary));
        Assert.Equal(new byte[] { 1, 2, 3 }, entry.Request);
        Assert.Equal(new byte[] { 0xAA, 1, 2, 3 }, entry.Response);
    }

    [Fact]
    public async Task Tcp_NoReplyFault_SuppressesTheReply_ButKeepsTheSession()
    {
        int port = FreePort.Get();
        var faults = new FaultInjector();
        await using var host = new SimulatorHost(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port }, new EchoEngine(), faults);
        Assert.True((await host.StartAsync()).IsSuccess);
        using var client = new TcpClient("127.0.0.1", port);
        NetworkStream stream = client.GetStream();

        faults.Plan = new FaultPlan { NoReply = true };
        await stream.WriteAsync(new byte[] { 9 });
        Assert.Null(await ReadAsync(stream, 300));

        faults.Plan = FaultPlan.None;
        await stream.WriteAsync(new byte[] { 7 });
        Assert.Equal(new byte[] { 0xAA, 7 }, await ReadAsync(stream, 2000));
    }

    [Fact]
    public async Task Tcp_DelayFault_HoldsTheReply()
    {
        int port = FreePort.Get();
        var faults = new FaultInjector();
        await using var host = new SimulatorHost(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port }, new EchoEngine(), faults);
        Assert.True((await host.StartAsync()).IsSuccess);
        using var client = new TcpClient("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        faults.Plan = new FaultPlan { DelayMilliseconds = 400 };

        var stopwatch = Stopwatch.StartNew();
        await stream.WriteAsync(new byte[] { 1 });

        Assert.Equal(new byte[] { 0xAA, 1 }, await ReadAsync(stream, 3000));
        Assert.True(stopwatch.ElapsedMilliseconds >= 350, $"reply came after {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Tcp_ForcedErrorCode_IsCarriedByTheEngineReply()
    {
        int port = FreePort.Get();
        var faults = new FaultInjector();
        await using var host = new SimulatorHost(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port }, new EchoEngine(), faults);
        Assert.True((await host.StartAsync()).IsSuccess);
        using var client = new TcpClient("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        faults.Plan = new FaultPlan { ForcedErrorCode = 0x0102 };

        await stream.WriteAsync(new byte[] { 5 });

        Assert.Equal(new byte[] { 0xEE, 0x02 }, await ReadAsync(stream, 2000));
    }

    [Fact]
    public async Task Tcp_DisconnectFault_ClosesTheSessionAfterTheNextRequest()
    {
        int port = FreePort.Get();
        var faults = new FaultInjector();
        await using var host = new SimulatorHost(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port }, new EchoEngine(), faults);
        Assert.True((await host.StartAsync()).IsSuccess);
        using var client = new TcpClient("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        faults.Plan = new FaultPlan { DisconnectOnNextRequest = true };

        await stream.WriteAsync(new byte[] { 1 });

        Assert.Empty(await ReadAsync(stream, 3000) ?? [0xFF]);
    }

    [Fact]
    public async Task Udp_RepliesToTheSenderOfEachDatagram()
    {
        int port = FreePort.Get();
        await using var host = new SimulatorHost(new UdpChannelConfig { LocalAddress = "127.0.0.1", LocalPort = port }, new EchoEngine(), new FaultInjector());
        Assert.True((await host.StartAsync()).IsSuccess);
        using var client = new UdpClient(0);

        await client.SendAsync(new byte[] { 4, 5 }, new IPEndPoint(IPAddress.Loopback, port));
        UdpReceiveResult reply = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(new byte[] { 0xAA, 4, 5 }, reply.Buffer);
    }

    [SerialPairFact]
    public async Task Serial_RepliesOnTheFirstPort_ToRequestsOnTheSecond()
    {
        (string first, string second) = SerialPairEnvironment.Read();
        await using var host = new SimulatorHost(new SerialChannelConfig { PortName = first, BaudRate = 115200 }, new EchoEngine(), new FaultInjector());
        Assert.True((await host.StartAsync()).IsSuccess);
        using var peer = new SerialPort(second, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 2000 };
        peer.Open();

        peer.Write(new byte[] { 6, 7 }, 0, 2);
        byte[] reply = new byte[3];
        int received = 0;
        while (received < reply.Length)
        {
            received += peer.Read(reply, received, reply.Length - received);
        }

        Assert.Equal(new byte[] { 0xAA, 6, 7 }, reply);
    }

    /// <summary>读取一段数据：超时返回 null，连接已关闭返回空数组。</summary>
    private static async Task<byte[]?> ReadAsync(Stream stream, int timeoutMilliseconds)
    {
        var buffer = new byte[256];
        using var cancellation = new CancellationTokenSource(timeoutMilliseconds);
        try
        {
            int count = await stream.ReadAsync(buffer, cancellation.Token);
            return count == 0 ? Array.Empty<byte>() : buffer[..count];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
