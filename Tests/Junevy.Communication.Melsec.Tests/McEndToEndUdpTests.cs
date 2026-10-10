using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 端到端（UDP，计划 Task 12）：3E / 4E × 二进制 / ASCII 的类型化读写；4E 迟到应答被丢弃，
/// 下一个请求拿到自己的应答（序列号匹配）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class McEndToEndUdpTests
{
    [Theory(Timeout = 60000)]
    [InlineData(McFrameType.Frame3E, McEncoding.Binary)]
    [InlineData(McFrameType.Frame3E, McEncoding.Ascii)]
    [InlineData(McFrameType.Frame4E, McEncoding.Binary)]
    [InlineData(McFrameType.Frame4E, McEncoding.Ascii)]
    public async Task TypedReadsAndWritesRoundTrip(McFrameType frameType, McEncoding encoding)
    {
        var options = new McClientOptions { FrameType = frameType, Encoding = encoding };
        var adapter = new McDeviceAdapter(frameType, encoding, sumCheck: true, new PlcMemory());
        await using DeviceSimulatorUdpHost host = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle).HostUdp();
        using McClient client = McClient.CreateUdp(new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = host.Port }, options);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await McEndToEndSupport.AssertTypedRoundTripAsync(client);
    }

    [Fact(Timeout = 60000)]
    public async Task Frame4ELateReply_IsDiscarded_AndTheNextRequestGetsItsOwnReply()
    {
        // 第一个请求的应答延迟 800 ms，超过客户端的 300 ms 超时；此后的请求必须得到自己的应答（值 2222H），而不是迟到的 1111H。
        var options = new McClientOptions { FrameType = McFrameType.Frame4E };
        var memory = new PlcMemory();
        memory.SetWords("D", 100, 0x1111);
        var adapter = new McDeviceAdapter(McFrameType.Frame4E, McEncoding.Binary, sumCheck: true, memory);
        int requests = 0;
        await using DeviceSimulatorUdpHost host = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle)
            .HostUdp(_ => Interlocked.Increment(ref requests) == 1 ? 800 : 0);
        using McClient client = McClient.CreateUdp(
            new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = host.Port, RequestTimeout = 300 }, options);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        CommResult<short> late = await client.ReadInt16Async("D100");
        Assert.Equal(CommErrorKind.Timeout, late.ErrorKind);

        memory.SetWords("D", 100, 0x2222);
        CommResult<short> next = await client.ReadInt16Async("D100");

        Assert.True(next.IsSuccess, next.ErrorMessage);
        Assert.Equal((short)0x2222, next.Data);
    }
}
