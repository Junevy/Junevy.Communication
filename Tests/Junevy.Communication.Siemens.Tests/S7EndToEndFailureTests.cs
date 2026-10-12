using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens.Protocol;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7RequestFrame;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 端到端的故障与生命周期（计划 Task 10 第 2 条，真套接字）：设备侧三种错误码与消息、断开后重连要重新握手、
/// 静默设备上的心跳失效、应答引用号不符，以及在途请求期间 Dispose。
/// 等待一律走事件与任务完成源，不轮询也不睡眠（计划第 1 节第 12 条）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class S7EndToEndFailureTests
{
    /// <summary>在途请求期间 Dispose 的循环次数（计划 Task 10 第 2 条）。</summary>
    private const int InFlightCycles = 20;

    private const string ItemDoesNotExistMessage = "S7 item return code 0x0A (object does not exist). " + PutGetHint;
    private const string InvalidAddressMessage = "S7 item return code 0x05 (invalid address). " + PutGetHint;
    private const string PutGetDisabledMessage = "S7 error class 0x81 code 0x04. " + PutGetHint;

    [Fact(Timeout = 60000)]
    public async Task UnknownDataBlock_IsItemCode0AOnBothDirections()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();

        // 替身只有 DB1 一块数据内存：DB99 没有对象，读写都按 17.3 节的 0x0A 回答。
        AssertRemoteError(await cpu.Client.ReadBytesAsync("DB99.DBB0", 2), 0x0A, ItemDoesNotExistMessage);
        AssertRemoteError(await cpu.Client.WriteBytesAsync("DB99.DBB0", new byte[] { 0x01, 0x02 }), 0x0A, ItemDoesNotExistMessage);
    }

    [Fact(Timeout = 60000)]
    public async Task AccessBeyondTheBlockEnd_IsItemCode05OnBothDirections()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();

        // 块长 512 字节：从 510 起访问 4 字节越界。地址本身合法，因此套件的 ValidateRange 放行、设备拒绝。
        AssertRemoteError(await cpu.Client.ReadBytesAsync("DB1.DBB510", 4), 0x05, InvalidAddressMessage);
        AssertRemoteError(await cpu.Client.WriteBytesAsync("DB1.DBB510", new byte[4]), 0x05, InvalidAddressMessage);
    }

    [Fact(Timeout = 60000)]
    public async Task PutGetDisabled_IsHeaderError0x8104OnBothDirections()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
        cpu.Device.PutGetDisabled = true;

        AssertRemoteError(await cpu.Client.ReadBytesAsync("DB1.DBB0", 2), 0x8104, PutGetDisabledMessage);
        AssertRemoteError(await cpu.Client.WriteInt16Async("DB1.DBW0", 1), 0x8104, PutGetDisabledMessage);
    }

    [Fact(Timeout = 60000)]
    public async Task Reconnect_HandshakesAgain()
    {
        var device = new S7DeviceSimulator();
        var secondConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScriptedTcpServer server = ScriptedTcpServer.Start(async (index, stream, token) =>
        {
            if (index == 1)
                secondConnection.TrySetResult(true);

            await device.ToDevice().RunAsync(stream, token);
        });
TcpClientChannelConfig transport = LoopbackTcp.Reconnecting(server.Port, reconnectIntervalMs: 100);
using S7Client client = S7Client.Create(transport, new S7ClientOptions());

        Succeeded(await client.ConnectAsync());
        Succeeded(await client.WriteInt16Async("DB1.DBW0", 7));
        Assert.Equal(1, device.ConnectionRequestCount);

        server.CloseConnection(0);

        // 重连重新走 CR → CC → Setup：替身统计到两次连接请求，旧值仍从同一块内存读回。
        await Arrival.WithinAsync(secondConnection.Task, 15000);
        Assert.True(await client.WaitForConnectedAsync(15000), "The channel did not finish reconnecting.");
        Assert.Equal(2, device.ConnectionRequestCount);
        Assert.Equal((short)7, Succeeded(await client.ReadInt16Async("DB1.DBW0")));
    }

    [Fact(Timeout = 60000)]
    public async Task SilentDeviceWithHeartbeat_EntersReconnectingWithHeartbeatFailed()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync(configureTransport: transport =>
        {
            transport.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 200, MaxFailures = 2 };
            transport.Reconnect = new ReconnectOptions { Enabled = true, Interval = 200 };
        });
        var failed = new TaskCompletionSource<ConnectionStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        cpu.Client.StateChanged += (sender, args) =>
        {
            if (args.Reason == DisconnectReason.HeartbeatFailed)
                failed.TrySetResult(args);
        };

        // 心跳探测读 MB0（P4-D15）：握手之后设备不再回答任何请求，连续失败达到上限即判为心跳失效。
        cpu.Device.Silent = true;

        ConnectionStateChangedEventArgs change = await Arrival.WithinAsync(failed.Task, 15000);

        Assert.Equal(ConnectionState.Reconnecting, change.CurrentState);
        Assert.Equal(DisconnectReason.HeartbeatFailed, change.Reason);
    }

    [Fact(Timeout = 60000)]
    public async Task ResponseWithADifferentReference_IsAProtocolViolation()
    {
        await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
        Succeeded(await cpu.Client.WriteInt16Async("DB1.DBW0", 3));
        cpu.Device.ForeignReference = 0x5AA5;

        CommResult<byte[]> result = await cpu.Client.ReadBytesAsync("DB1.DBB0", 2);

        // P4-D8：应答的引用号与请求不符就是帧结构错误；RetryCount 默认 0，因此一次即返回。
        AssertViolation(result);
        Assert.Contains("0x5AA5", result.ErrorMessage);
    }

    [Fact(Timeout = 60000)]
    public async Task Dispose_WhileARequestIsInFlight_FailsItWithConnectionClosed()
    {
        for (int iteration = 0; iteration < InFlightCycles; iteration++)
        {
            await using S7ConnectedCpu cpu = await S7ConnectedCpu.StartAsync();
            cpu.Device.Silent = true;

            Task<byte[]> arrived = cpu.Device.NextRequestAsync();
            Task<CommResult<byte[]>> pending = cpu.Client.ReadBytesAsync("DB1.DBB0", 4);

            // 等设备确实收到读请求再 Dispose：否则命中的是"Dispose 之后才调用"的契约，而不是在途请求。
            byte[] request = await Arrival.WithinAsync(arrived, 15000);
            Assert.Equal(S7VarItem.ReadService, Service(request));

            cpu.Client.Dispose();

            // 在途请求由通道的停止序列判为连接关闭：不抛异常、不悬挂，也不因 Dispose 而重试。
            CommResult<byte[]> result = await Arrival.WithinAsync(pending, 15000);
            Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        }
    }
}
