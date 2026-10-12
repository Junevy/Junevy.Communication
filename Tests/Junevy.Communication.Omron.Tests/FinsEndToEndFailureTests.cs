using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 端到端的故障与生命周期（计划 Task 14 第 2 条，真套接字）：设备侧的越界结束码、可配置的非致命标志 0x0040、
/// FINS/TCP 头错误码、应答 SID 与请求不符、断开后重新握手并跟随新派节点号、静默设备上的心跳失效，
/// 以及在途请求期间 Dispose。等待一律走事件与任务完成源，不轮询也不睡眠（计划第 1 节第 12 条）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FinsEndToEndFailureTests
{
    /// <summary>在途请求期间 Dispose 的循环次数（计划 Task 14 第 2 条）。</summary>
    private const int InFlightCycles = 20;

    /// <summary>17.2 节 V-F-8：地址越界的结束码，替身按区容量判定。</summary>
    private const ushort OutOfRangeEndCode = 0x1103;

    /// <summary>P4-D14 里 FINS/TCP 头的错误码示例（外壳非 0 即设备侧错误）。</summary>
    private const uint TcpErrorCode = 0x00000002;

    /// <summary>替身在第二次握手时另派的客户端节点号（P4-D18）。</summary>
    private const byte ReassignedClientNode = 0x0C;

    [Fact(Timeout = 60000)]
    public async Task AccessBeyondTheAreaCapacity_IsRemoteError1103OnBothDirections()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();

        // 字号本身合法（远小于 0xFFFF），套件的 ValidateRange 放行，越界由设备按 V-F-8 的结束码拒绝。
        string beyond = $"D{FinsDeviceSimulator.AreaCapacity}";
        AssertRemoteError(await plc.Client.ReadAsync(beyond, 2), OutOfRangeEndCode, EndCodeMessage(OutOfRangeEndCode));
        AssertRemoteError(await plc.Client.WriteAsync(beyond, ToBytes("00 01 02 03")), OutOfRangeEndCode, EndCodeMessage(OutOfRangeEndCode));
    }

    [Fact(Timeout = 60000)]
    public async Task NonFatalEndCode_0x0040_IsStillSuccess()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        plc.Device.Memory.SetWords("D", 100, 0x002A);
        plc.Device.NonFatalEndCode = true;

        // P4-D14、V-F-9：主码 & 0x7F 与子码 & 0x3F 之后为 0 即成功，因此 00 40 不是错误且数据照读。
        Assert.Equal(new[] { (ushort)0x002A }, Succeeded(await plc.Client.ReadUInt16Async("D100", 1)));
        Succeeded(await plc.Client.WriteInt16Async("D100", 0x002B));
        Assert.Equal(new[] { (ushort)0x002B }, plc.Device.Memory.GetWords("D", 100, 1));
    }

    [Fact(Timeout = 60000)]
    public async Task TcpErrorCode_IsRemoteErrorBeforeTheFrameIsInspected()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        plc.Device.TcpError = TcpErrorCode;

        // P4-D14：外壳的错误码排在结构判断之前，出错时设备可以不带完整的 FINS 帧。
        AssertRemoteError(await plc.Client.ReadAsync("D100", 1), TcpErrorCode, TcpErrorMessage(TcpErrorCode));
    }

    [Fact(Timeout = 60000)]
    public async Task ResponseWithADifferentSessionId_IsAProtocolViolation()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
        Succeeded(await plc.Client.WriteInt16Async("D0", 3));
        plc.Device.ForeignSessionId = 0x5A;

        // P4-D8、P4-D17：应答的 SID 与请求不符就是帧结构错误；RetryCount 默认 0，一次即返回。
        CommResult<short> result = await plc.Client.ReadInt16Async("D0");
        AssertViolation(result);
        Assert.Contains("0x5A", result.ErrorMessage);
    }

    [Fact(Timeout = 60000)]
    public async Task Reconnect_HandshakesAgain_AndTheNewClientNodeBecomesTheSource()
    {
        var device = new FinsDeviceSimulator();
        var secondConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedTcpServer server = ScriptedTcpServer.Start(async (index, stream, token) =>
        {
            if (index == 1)
                secondConnection.TrySetResult(true);

            await device.ToDevice().RunAsync(stream, token);
        });
        var transport = new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 100 },
        };
        using FinsClient client = FinsClient.Create(transport, new FinsClientOptions());

        Succeeded(await client.ConnectAsync());
        Succeeded(await client.WriteInt16Async("D0", 7));
        Assert.Equal(1, device.HandshakeCount);
        Assert.Equal(ClientNode, client.ClientNode);

        server.CloseConnection(0);

        // 重连重走节点地址握手；PLC 另派新号，之后的请求都以新号为 SA1（P4-D18）。
        await ArrivesAsync(secondConnection.Task, 15000);
        Assert.True(await client.WaitForConnectedAsync(15000), "The channel did not finish reconnecting.");
        Assert.Equal(2, device.HandshakeCount);
        Succeeded(await client.WriteInt16Async("D0", 8));

        Assert.Equal(ReassignedClientNode, client.ClientNode!.Value);
        Assert.Equal(ReassignedClientNode, device.Requests.Last()[SourceNodeIndex]);
        Assert.Equal((short)8, Succeeded(await client.ReadInt16Async("D0")));
    }

    [Fact(Timeout = 60000)]
    public async Task SilentDeviceWithHeartbeat_EntersReconnectingWithHeartbeatFailed()
    {
        await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync(configureTransport: transport =>
        {
            transport.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 200, MaxFailures = 2 };
            transport.Reconnect = new ReconnectOptions { Enabled = true, Interval = 200 };
        });
        var failed = new TaskCompletionSource<ConnectionStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        plc.Client.StateChanged += (sender, args) =>
        {
            if (args.Reason == DisconnectReason.HeartbeatFailed)
                failed.TrySetResult(args);
        };

        // 心跳探测读 D0 的 1 字（P4-D15）：握手之后设备不再回答任何请求，连续失败达到上限即判为心跳失效。
        plc.Device.Silent = true;

        ConnectionStateChangedEventArgs change = await ArrivesAsync(failed.Task, 15000);

        Assert.Equal(ConnectionState.Reconnecting, change.CurrentState);
        Assert.Equal(DisconnectReason.HeartbeatFailed, change.Reason);
    }

    [Fact(Timeout = 60000)]
    public async Task Dispose_WhileARequestIsInFlight_FailsItWithConnectionClosed()
    {
        for (int iteration = 0; iteration < InFlightCycles; iteration++)
        {
            await using FinsConnectedPlc plc = await FinsConnectedPlc.StartAsync();
            plc.Device.Silent = true;

            Task<byte[]> arrived = plc.Device.NextRequestAsync();
            Task<CommResult<byte[]>> pending = plc.Client.ReadAsync("D0", 2);

            // 等设备确实收到读请求再 Dispose：否则命中的是"Dispose 之后才调用"的契约，而不是在途请求。
            byte[] request = await ArrivesAsync(arrived, 15000);
            Assert.Equal(FinsMemoryArea.CommandRead, ServiceOf(request));

            plc.Client.Dispose();

            // 在途请求由通道的停止序列判为连接关闭：不抛异常、不悬挂，也不因 Dispose 而重试。
            CommResult<byte[]> result = await ArrivesAsync(pending, 15000);
            Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        }
    }

    /// <summary>在时限内等待一个任务完成；超时即失败（不轮询、不睡眠）。</summary>
    private static async Task<T> ArrivesAsync<T>(Task<T> pending, int withinMs)
    {
        Task finished = await Task.WhenAny(pending, Task.Delay(withinMs));
        Assert.True(ReferenceEquals(finished, pending), $"Nothing arrived within {withinMs} ms.");
        return await pending;
    }
}
