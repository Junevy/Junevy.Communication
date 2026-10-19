using System.Threading;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Modbus.Tests.EndToEnd;

/// <summary>
/// 端到端（UDP，P7 Task 10）：MBAP 与 RTU over UDP 的读写；数据报不分帧，迟到的应答不影响下一个请求（每个请求独立）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class ModbusEndToEndUdpTests
{
    [Theory(Timeout = 60000)]
    [InlineData(ModbusFraming.Mbap)]
    [InlineData(ModbusFraming.Rtu)]
    public async Task FourAreas_ReadAndWriteRoundTrip(ModbusFraming framing)
    {
        var memory = new PlcMemory();
        memory.SetWords("IR", 5, 0x0042);
        memory.SetBits("DI", 7, true);
        var slave = new ModbusSlave(framing, memory);
        await using DeviceSimulatorUdpHost host = new DeviceSimulator(ModbusDeviceCodecs.For(framing), slave.Handle).HostUdp();
        using ModbusClient client = await ConnectAsync(host.Port, framing, 2000);

        await ModbusScenarios.FourAreasRoundTripAsync(client);
    }

    [Theory(Timeout = 60000)]
    [InlineData(ModbusFraming.Mbap)]
    [InlineData(ModbusFraming.Rtu)]
    public async Task LateReply_IsDiscarded_AndTheNextRequestGetsItsOwnReply(ModbusFraming framing)
    {
        var memory = new PlcMemory();
        memory.SetWords("HR", 0, 0x1111);
        var slave = new ModbusSlave(framing, memory);
        int requests = 0;
        await using DeviceSimulatorUdpHost host = new DeviceSimulator(ModbusDeviceCodecs.For(framing), slave.Handle)
            .HostUdp(_ => Interlocked.Increment(ref requests) == 1 ? 800 : 0);
        using ModbusClient client = await ConnectAsync(host.Port, framing, 300);

        Assert.Equal(CommErrorKind.Timeout, (await client.ReadUInt16Async("HR0")).ErrorKind);

        memory.SetWords("HR", 0, 0x2222);
        CommResult<ushort> next = await client.ReadUInt16Async("HR0");

        Assert.True(next.IsSuccess, next.ErrorMessage);
        Assert.Equal((ushort)0x2222, next.Data);
    }

    private static async Task<ModbusClient> ConnectAsync(int port, ModbusFraming framing, int requestTimeout)
    {
        var options = new ModbusClientOptions { Framing = framing };
        ModbusClient client = ModbusClient.Create(new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = port, RequestTimeout = requestTimeout }, options);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        return client;
    }
}
