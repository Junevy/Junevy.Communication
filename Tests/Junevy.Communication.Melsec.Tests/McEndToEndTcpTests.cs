using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 端到端（TCP，计划 Task 12）：3E / 4E × 二进制 / ASCII 的类型化读写，拆包（2000 字读、8000 点位读），
/// 设备返回结束码，服务端断开后重连，静默设备上的心跳失败。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class McEndToEndTcpTests
{
    private const int WordBatch = 960;          // 字访问每包上限（计划 §14 / §18 的上限表）
    private const int BinaryBitBatch = 7168;    // 以太网二进制位访问每包上限

    [Theory(Timeout = 60000)]
    [InlineData(McFrameType.Frame3E, McEncoding.Binary)]
    [InlineData(McFrameType.Frame3E, McEncoding.Ascii)]
    [InlineData(McFrameType.Frame4E, McEncoding.Binary)]
    [InlineData(McFrameType.Frame4E, McEncoding.Ascii)]
    public async Task TypedReadsAndWritesRoundTrip(McFrameType frameType, McEncoding encoding)
    {
        var options = new McClientOptions { FrameType = frameType, Encoding = encoding };
        var adapter = new McDeviceAdapter(frameType, encoding, sumCheck: true, new PlcMemory());
        await using DeviceSimulatorTcpHost host = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle).HostTcp();
        using McClient client = McClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = host.Port }, options);

        Assert.True((await client.ConnectAsync()).IsSuccess);
        await McEndToEndSupport.AssertTypedRoundTripAsync(client);
    }

    [Fact(Timeout = 60000)]
    public async Task WordReadOf2000Points_IsSplitIntoPacketsOf960Words()
    {
        var options = new McClientOptions();
        var adapter = new McDeviceAdapter(McFrameType.Frame3E, McEncoding.Binary, sumCheck: true, new PlcMemory());
        await using DeviceSimulatorTcpHost host = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle).HostTcp();
        using McClient client = McClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = host.Port }, options);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        CommResult<short[]> result = await client.ReadInt16Async("D0", 2000);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(2000, result.Data!.Length);
        Assert.Equal(new[] { WordBatch, WordBatch, 80 }, adapter.Batches.Select(batch => batch.Points).ToArray());
        Assert.Equal(new[] { 0, WordBatch, 2 * WordBatch }, adapter.Batches.Select(batch => batch.Number).ToArray());
    }

    [Fact(Timeout = 60000)]
    public async Task BitReadOf8000Points_IsSplitIntoBinaryPackets()
    {
        var options = new McClientOptions();
        var adapter = new McDeviceAdapter(McFrameType.Frame3E, McEncoding.Binary, sumCheck: true, new PlcMemory());
        await using DeviceSimulatorTcpHost host = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle).HostTcp();
        using McClient client = McClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = host.Port }, options);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        CommResult<bool[]> result = await client.ReadBitsAsync("M0", 8000);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(8000, result.Data!.Length);
        Assert.Equal(new[] { BinaryBitBatch, 8000 - BinaryBitBatch }, adapter.Batches.Select(batch => batch.Points).ToArray());
    }

    [Fact(Timeout = 60000)]
    public async Task EndCodeFromTheDevice_IsARemoteErrorWithTheRawCode()
    {
        var options = new McClientOptions();
        var adapter = new McDeviceAdapter(McFrameType.Frame3E, McEncoding.Binary, sumCheck: true, new PlcMemory()) { EndCodeOverride = 0xC051 };
        await using DeviceSimulatorTcpHost host = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle).HostTcp();
        using McClient client = McClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = host.Port }, options);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        CommResult<short> result = await client.ReadInt16Async("D100");

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(0xC051L, result.ProtocolErrorCode);
    }

    [Fact(Timeout = 60000)]
    public async Task ServerClosesTheConnection_TheClientReconnectsAndReadsAgain()
    {
        var options = new McClientOptions { RetryCount = 3, RetryInterval = 2000 };
        var memory = new PlcMemory();
        memory.SetWords("D", 100, 7);
        var adapter = new McDeviceAdapter(McFrameType.Frame3E, McEncoding.Binary, sumCheck: true, memory);
        DeviceSimulator simulator = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle);
        using ScriptedTcpServer server = ScriptedTcpServer.Start((index, stream, token) => simulator.RunAsync(stream, token));
TcpClientChannelConfig config = LoopbackTcp.Reconnecting(server.Port, reconnectIntervalMs: 100);
using McClient client = McClient.Create(config, options);
Assert.True((await client.ConnectAsync()).IsSuccess);
        Assert.Equal((short)7, (await client.ReadInt16Async("D100")).Data);

        server.CloseConnection(0);
        // 等待重连完成再读取：重连期间的读取按 P2-D7 立即返回 NotConnected，不会重试。
        await Polling.WaitUntilAsync(() => server.AcceptedConnectionCount == 2 && client.IsConnected, 10000);
        CommResult<short> result = await client.ReadInt16Async("D100");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal((short)7, result.Data);
        Assert.Equal(2, server.AcceptedConnectionCount);
    }

    [Fact(Timeout = 60000)]
    public async Task SilentDeviceWithHeartbeat_EntersReconnectingWithHeartbeatFailed()
    {
using SilentTcpServer silent = SilentTcpServer.Start();
        TcpClientChannelConfig config = LoopbackTcp.WithHeartbeat(silent.Port, intervalMs: 100, timeoutMs: 100, maxFailures: 2, reconnectIntervalMs: 200);
using McClient client = McClient.Create(config, new McClientOptions());
        var changes = new List<ConnectionStateChangedEventArgs>();
        client.StateChanged += (sender, args) =>
        {
            lock (changes)
                changes.Add(args);
        };

        Assert.True((await client.ConnectAsync()).IsSuccess);

        await Polling.WaitUntilAsync(() =>
        {
            lock (changes)
                return changes.Any(args => args.CurrentState == ConnectionState.Reconnecting && args.Reason == DisconnectReason.HeartbeatFailed);
        }, 15000);
    }
}
