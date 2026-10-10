using System.Net;
using System.Net.Sockets;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 测试辅助库的单测（计划 Task 7 第 6 条）：<see cref="PlcMemory"/> 与 UDP 模拟设备宿主。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class TestingKitTests
{
    private const int ReceiveTimeoutMs = 5000;

    [Fact(Timeout = 20000)]
    public void PlcMemory_UnwrittenWordsAndBits_ReadAsZeroAndFalse()
    {
        var memory = new PlcMemory();

        Assert.Equal(new ushort[] { 0, 0, 0 }, memory.GetWords("D", 100, 3));
        Assert.Equal(new[] { false, false }, memory.GetBits("M", 0, 2));
    }

    [Fact(Timeout = 20000)]
    public void PlcMemory_WordsRoundTrip_AndDeviceSymbolsAreCaseInsensitive()
    {
        var memory = new PlcMemory();

        memory.SetWords("d", 100, 0x1234, 0x0002, 0xFFFF);

        Assert.Equal(new ushort[] { 0x1234, 0x0002 }, memory.GetWords("D", 100, 2));
        Assert.Equal(new ushort[] { 0xFFFF }, memory.GetWords("D", 102, 1));
        Assert.Equal(new ushort[] { 0, 0x1234 }, memory.GetWords("D", 99, 2));
    }

    [Fact(Timeout = 20000)]
    public void PlcMemory_BitsAndWords_AreSeparateSpaces()
    {
        var memory = new PlcMemory();

        memory.SetBits("D", 100, true);
        memory.SetWords("D", 100, 0x0007);

        Assert.True(memory.GetBits("D", 100, 1)[0]);
        Assert.Equal((ushort)0x0007, memory.GetWords("D", 100, 1)[0]);
    }

    [Fact(Timeout = 20000)]
    public void PlcMemory_GetBitsAsWords_PacksSixteenPointsWithTheFirstPointInBitZero()
    {
        var memory = new PlcMemory();
        bool[] first = new bool[16];
        first[0] = true;
        first[15] = true;
        memory.SetBits("M", 0, first);
        memory.SetBits("M", 17, true);

        Assert.Equal(new ushort[] { 0x8001, 0x0002 }, memory.GetBitsAsWords("M", 0, 2));
    }

    [Fact(Timeout = 20000)]
    public void PlcMemory_SetBitsFromWords_ReadsBackAsPoints()
    {
        var memory = new PlcMemory();

        memory.SetBitsFromWords("M", 16, 0x8001);

        Assert.True(memory.GetBits("M", 16, 1)[0]);
        Assert.False(memory.GetBits("M", 17, 1)[0]);
        Assert.True(memory.GetBits("M", 31, 1)[0]);
        Assert.Equal(new ushort[] { 0x8001 }, memory.GetBitsAsWords("M", 16, 1));
    }

    [Fact(Timeout = 20000)]
    public async Task PlcMemory_ConcurrentWrites_AreAllKept()
    {
        var memory = new PlcMemory();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
                memory.SetWords("D", worker * 500 + i, (ushort)(worker + 1));
        })));

        ushort[] values = memory.GetWords("D", 0, 8 * 500);
        Assert.All(values, value => Assert.NotEqual((ushort)0, value));
    }

    [Fact(Timeout = 20000)]
    public async Task UdpHost_RepliesToTheSenderWithTheHandlerResult()
    {
        await using DeviceSimulatorUdpHost host = Simulator(request => Encoding.ASCII.GetBytes("pong")).HostUdp();
        using UdpClient client = NewClient();

        await SendAsync(client, host.Port, "ping");

        Assert.Equal("pong", await ReceiveTextAsync(client));
    }

    [Fact(Timeout = 20000)]
    public async Task UdpHost_NullResult_SendsNoReply_AndKeepsListening()
    {
        await using DeviceSimulatorUdpHost host = Simulator(request => Encoding.ASCII.GetString(request) == "reply"
            ? Encoding.ASCII.GetBytes("ok")
            : null).HostUdp();
        using UdpClient client = NewClient();

        await SendAsync(client, host.Port, "silent");
        await SendAsync(client, host.Port, "reply");

        // "silent" 没有回复，因此第一个到达的回复必须是 "ok"。
        Assert.Equal("ok", await ReceiveTextAsync(client));
    }

    [Fact(Timeout = 20000)]
    public async Task UdpHost_ReplyDelay_DelaysOnlyTheDelayedReply()
    {
        await using DeviceSimulatorUdpHost host = Simulator(request => request)
            .HostUdp(request => Encoding.ASCII.GetString(request) == "slow" ? 300 : 0);
        using UdpClient client = NewClient();

        await SendAsync(client, host.Port, "slow");
        await SendAsync(client, host.Port, "fast");

        Assert.Equal("fast", await ReceiveTextAsync(client));
        Assert.Equal("slow", await ReceiveTextAsync(client));
    }

    [Fact(Timeout = 20000)]
    public async Task UdpHost_HandlerException_DoesNotStopTheHost()
    {
        await using DeviceSimulatorUdpHost host = Simulator(request =>
        {
            if (Encoding.ASCII.GetString(request) == "boom")
                throw new InvalidOperationException("The test device failed.");

            return Encoding.ASCII.GetBytes("ok");
        }).HostUdp();
        using UdpClient client = NewClient();

        await SendAsync(client, host.Port, "boom");
        await SendAsync(client, host.Port, "ping");

        Assert.Equal("ok", await ReceiveTextAsync(client));
    }

    [Fact(Timeout = 20000)]
    public async Task UdpHost_PortIsAssignedOnLoopback_AndDisposeIsIdempotent()
    {
        DeviceSimulatorUdpHost host = Simulator(request => request).HostUdp();

        Assert.InRange(host.Port, 1, 65535);
        await host.DisposeAsync();
        await host.DisposeAsync();
    }

    // 数据报不分帧，因此分帧规则只是 DeviceSimulator 的构造参数，这里使用原样分帧。
    private static DeviceSimulator Simulator(Func<byte[], byte[]?> handler)
        => new DeviceSimulator(FrameCodecFactory.Create(new FramingOptions()), handler);

    private static UdpClient NewClient() => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

    private static Task<int> SendAsync(UdpClient client, int port, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        return client.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<string> ReceiveTextAsync(UdpClient client)
    {
        Task<UdpReceiveResult> receive = client.ReceiveAsync();
        Task finished = await Task.WhenAny(receive, Task.Delay(ReceiveTimeoutMs));
        Assert.Same(receive, finished);

        return Encoding.ASCII.GetString((await receive).Buffer);
    }
}
