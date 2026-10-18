using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.EndToEnd;

/// <summary>
/// 端到端（TCP，P7 Task 10）：MBAP 与 RTU over TCP 的读写、拆包、类型化值、站号轮询、异常码、迟到应答（MBAP 不重建连接，RTU 重建）、
/// 噪声字节、心跳与重连、请求中释放、重试与掩码写。模拟从站在回环地址上运行。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class ModbusEndToEndTcpTests
{
    private const int ShortTimeout = 300;

    [Theory(Timeout = 60000)]
    [InlineData(ModbusFraming.Mbap)]
    [InlineData(ModbusFraming.Rtu)]
    public async Task FourAreas_ReadAndWriteRoundTrip(ModbusFraming framing)
    {
        var memory = new PlcMemory();
        memory.SetWords("IR", 5, 0x0042);
        memory.SetBits("DI", 7, true);
        await using var slave = new ModbusTcpSlave(framing, new ModbusSlave(framing, memory));
        using ModbusClient client = await ConnectAsync(slave.Port, framing);

        Assert.True((await client.WriteUInt16Async("HR10", 0x1234)).IsSuccess);
        Assert.Equal((ushort)0x1234, (await client.ReadUInt16Async("HR10")).Data);
        Assert.Equal((ushort)0x0042, (await client.ReadUInt16Async("IR5")).Data);
        Assert.True((await client.WriteBoolAsync("C3", true)).IsSuccess);
        Assert.True((await client.ReadBoolAsync("C3")).Data);
        Assert.True((await client.ReadBoolAsync("DI7")).Data);
    }

    [Theory(Timeout = 60000)]
    [InlineData(ModbusFraming.Mbap)]
    [InlineData(ModbusFraming.Rtu)]
    public async Task PacketBoundaries_RoundTripAcrossSeveralPackets(ModbusFraming framing)
    {
        await using var slave = new ModbusTcpSlave(framing, new ModbusSlave(framing, new PlcMemory(), limit: 10000));
        using ModbusClient client = await ConnectAsync(slave.Port, framing);
        ushort[] words = Enumerable.Range(1, 126).Select(i => (ushort)i).ToArray();
        bool[] bits = Enumerable.Range(0, 2001).Select(i => i % 3 == 0).ToArray();

        Assert.True((await client.WriteUInt16Async("HR0", words)).IsSuccess);
        Assert.Equal(words, (await client.ReadUInt16Async("HR0", 126)).Data);
        Assert.True((await client.WriteBitsAsync("C0", bits)).IsSuccess);
        Assert.Equal(bits, (await client.ReadBitsAsync("C0", 2001)).Data);
    }

    [Theory(Timeout = 60000)]
    [InlineData(ModbusFraming.Mbap)]
    [InlineData(ModbusFraming.Rtu)]
    public async Task TypedValues_RoundTrip(ModbusFraming framing)
    {
        await using var slave = new ModbusTcpSlave(framing, new ModbusSlave(framing, new PlcMemory()));
        using ModbusClient client = await ConnectAsync(slave.Port, framing);

        Assert.True((await client.WriteInt32Async("HR20", -123456)).IsSuccess);
        Assert.Equal(-123456, (await client.ReadInt32Async("HR20")).Data);
        Assert.True((await client.WriteFloatAsync("HR30", 1.5f)).IsSuccess);
        Assert.Equal(1.5f, (await client.ReadFloatAsync("HR30")).Data);
        Assert.True((await client.WriteStringAsync("HR40", "ABCD", 4)).IsSuccess);
        Assert.Equal("ABCD", (await client.ReadStringAsync("HR40", 4)).Data);
    }

    [Theory(Timeout = 60000)]
    [InlineData(ModbusFraming.Mbap)]
    [InlineData(ModbusFraming.Rtu)]
    public async Task StationPolling_ReadsEachAnsweringStationAndTimesOutTheSilentOne(ModbusFraming framing)
    {
        var memory = new PlcMemory();
        memory.SetWords("HR", 0, 0x0100);
        var slave = new ModbusSlave(framing, memory);
        slave.Units.Clear();
        slave.Units.Add(1);
        slave.Units.Add(2);
        await using var host = new ModbusTcpSlave(framing, slave);
        using ModbusClient client = await ConnectAsync(host.Port, framing, ShortTimeout);

        Assert.True((await client.ReadUInt16Async("s=1;HR0")).IsSuccess);
        Assert.True((await client.ReadUInt16Async("s=2;HR0")).IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, (await client.ReadUInt16Async("s=3;HR0")).ErrorKind);
        if (framing == ModbusFraming.Rtu)
            await AwaitRebuiltConnectionAsync(client);
        Assert.Equal((ushort)0x0100, (await client.ReadUInt16Async("s=1;HR0")).Data);
    }

    [Theory(Timeout = 60000)]
    [InlineData(ModbusFraming.Mbap)]
    [InlineData(ModbusFraming.Rtu)]
    public async Task ExceptionCode02_IsRemoteError(ModbusFraming framing)
    {
        await using var slave = new ModbusTcpSlave(framing, new ModbusSlave(framing, new PlcMemory(), limit: 1000));
        using ModbusClient client = await ConnectAsync(slave.Port, framing);

        CommResult<byte[]> result = await client.ReadAsync("HR999", 2);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(2, result.ProtocolErrorCode);
    }

    [Fact(Timeout = 60000)]
    public async Task MbapLateReply_IsDiscarded_WithoutRebuildingTheConnection()
    {
        // 第一个回复延迟到超时之后才发；MBAP 按事务号关联，迟到的回复被丢弃，连接不重建。
        var memory = new PlcMemory();
        memory.SetWords("HR", 0, 0x1111);
        await using var slave = new ModbusTcpSlave(ModbusFraming.Mbap, new ModbusSlave(ModbusFraming.Mbap, memory),
            replyNumber => replyNumber == 1 ? 800 : 0);
        using ModbusClient client = await ConnectAsync(slave.Port, ModbusFraming.Mbap, ShortTimeout);

        Assert.Equal(CommErrorKind.Timeout, (await client.ReadUInt16Async("HR0")).ErrorKind);

        memory.SetWords("HR", 0, 0x2222);
        CommResult<ushort> next = await client.ReadUInt16Async("HR0");

        Assert.True(next.IsSuccess, next.ErrorMessage);
        Assert.Equal((ushort)0x2222, next.Data);
        Assert.Equal(1, slave.AcceptedConnectionCount);
    }

    [Fact(Timeout = 60000)]
    public async Task RtuOverTcpLateReply_RebuildsTheConnection_AndTheNextReadIsCorrect()
    {
        var memory = new PlcMemory();
        memory.SetWords("HR", 0, 0x1111);
        await using var slave = new ModbusTcpSlave(ModbusFraming.Rtu, new ModbusSlave(ModbusFraming.Rtu, memory),
            replyNumber => replyNumber == 1 ? 800 : 0);
        using ModbusClient client = await ConnectAsync(slave.Port, ModbusFraming.Rtu, ShortTimeout);

        Assert.Equal(CommErrorKind.Timeout, (await client.ReadUInt16Async("HR0")).ErrorKind);
        await AwaitRebuiltConnectionAsync(client);

        memory.SetWords("HR", 0, 0x2222);
        CommResult<ushort> next = await client.ReadUInt16Async("HR0");

        Assert.True(next.IsSuccess, next.ErrorMessage);
        Assert.Equal((ushort)0x2222, next.Data);
        Assert.Equal(2, slave.AcceptedConnectionCount);
    }

    [Fact(Timeout = 60000)]
    public async Task NoiseBeforeTheReply_IsSkipped()
    {
        var memory = new PlcMemory();
        memory.SetWords("HR", 0, 0x0A0B);
        var slave = new ModbusSlave(ModbusFraming.Rtu, memory) { NoisePrefix = new byte[] { 0xAA, 0xBB, 0xCC } };
        await using var host = new ModbusTcpSlave(ModbusFraming.Rtu, slave);
        using ModbusClient client = await ConnectAsync(host.Port, ModbusFraming.Rtu);

        CommResult<ushort> result = await client.ReadUInt16Async("HR0");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal((ushort)0x0A0B, result.Data);
    }

    [Fact(Timeout = 60000)]
    public async Task SilentDevice_WithHeartbeat_GoesReconnecting_AndRecovers()
    {
        var slave = new ModbusSlave(ModbusFraming.Mbap, new PlcMemory());
        slave.Units.Clear();
        await using var host = new ModbusTcpSlave(ModbusFraming.Mbap, slave);
        var options = new ModbusClientOptions { Framing = ModbusFraming.Mbap };
        var config = new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = host.Port,
            RequestTimeout = ShortTimeout,
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, MaxFailures = 2 },
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 50, MaxInterval = 200 },
        };
        using ModbusClient client = ModbusClient.Create(config, options);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        await Polling.WaitUntilAsync(() => client.State == ConnectionState.Reconnecting, 10000);

        slave.Units.Add(1);
        await Polling.WaitUntilAsync(() => client.State == ConnectionState.Connected, 10000);
        Assert.True((await client.ReadUInt16Async("HR0")).IsSuccess);
    }

    [Fact(Timeout = 60000)]
    public async Task DisposeDuringARequest_ReturnsAFailureWithoutThrowing()
    {
        for (int i = 0; i < 20; i++)
        {
            var slave = new ModbusSlave(ModbusFraming.Mbap, new PlcMemory());
            slave.Units.Clear();
            await using var host = new ModbusTcpSlave(ModbusFraming.Mbap, slave);
            ModbusClient client = await ConnectAsync(host.Port, ModbusFraming.Mbap, 5000);

            Task<CommResult<ushort>> pending = client.ReadUInt16Async("HR0");
            client.Dispose();
            CommResult<ushort> result = await pending;

            Assert.False(result.IsSuccess);
        }
    }

    // 只测 MBAP：RTU over TCP 的超时会重建连接，而重试可能早于重建完成（见 P7 计划第 25.2 节的发现），这里不把它当作期望。
    [Fact(Timeout = 60000)]
    public async Task RetryCountOne_FirstRequestDropped_SecondSucceeds()
    {
        const ModbusFraming framing = ModbusFraming.Mbap;
        var memory = new PlcMemory();
        memory.SetWords("HR", 0, 0x0777);
        var slave = new ModbusSlave(framing, memory) { DropRequest = number => number == 1 };
        await using var host = new ModbusTcpSlave(framing, slave);
        using ModbusClient client = await ConnectAsync(host.Port, framing, ShortTimeout, new ModbusClientOptions { RetryCount = 1 });

        CommResult<ushort> result = await client.ReadUInt16Async("HR0");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal((ushort)0x0777, result.Data);
    }

    [Fact(Timeout = 60000)]
    public async Task MaskWriteBit_LeavesTheOtherBitsOfTheRegisterUnchanged()
    {
        var memory = new PlcMemory();
        memory.SetWords("HR", 40, 0x00FF);
        await using var slave = new ModbusTcpSlave(ModbusFraming.Mbap, new ModbusSlave(ModbusFraming.Mbap, memory));
        using ModbusClient client = await ConnectAsync(slave.Port, ModbusFraming.Mbap, options: new ModbusClientOptions { UseMaskWriteForBits = true });

        Assert.True((await client.WriteBitsAsync("HR40.8", new[] { true })).IsSuccess);
        Assert.Equal((ushort)0x01FF, memory.GetWords("HR", 40, 1)[0]);

        Assert.True((await client.WriteBitsAsync("HR40.0", new[] { false })).IsSuccess);
        Assert.Equal((ushort)0x01FE, memory.GetWords("HR", 40, 1)[0]);
    }

    // RTU over TCP 超时后连接被重建（P7-D13）：先等待断开被观察到，再等待重新连接。
    private static async Task AwaitRebuiltConnectionAsync(ModbusClient client)
    {
        await Polling.WaitUntilAsync(() => !client.IsConnected, 5000);
        Assert.True(await client.WaitForConnectedAsync(5000));
    }

    private static async Task<ModbusClient> ConnectAsync(int port, ModbusFraming framing, int requestTimeout = 2000, ModbusClientOptions? options = null)
    {
        ModbusClientOptions effective = options ?? new ModbusClientOptions();
        effective.Framing = framing;
        // 超时后 RTU over TCP 会重建连接（ResetOnRequestTimeout）；重建由后台重连完成（P7-D13），因此这里启用重连。
        var config = new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = port,
            RequestTimeout = requestTimeout,
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 50, MaxInterval = 200 },
        };
        ModbusClient client = ModbusClient.Create(config, effective);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        return client;
    }
}
