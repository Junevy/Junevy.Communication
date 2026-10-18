using System.IO.Ports;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.EndToEnd;

/// <summary>
/// 端到端（串口，P7 Task 10）：<see cref="DuplexClientChannel"/> 与模拟从站通过内存管道相连，经 <see cref="ModbusProtocolProfile"/> 配置 RTU 分帧。
/// 另有一个真实串口对的用例，未设置 <c>JUNEVY_SERIAL_PAIR</c> 时跳过（执行者不运行，由审阅者运行）。
/// </summary>
public sealed class ModbusEndToEndSerialTests
{
    [Fact(Timeout = 60000)]
    public async Task FourAreas_OverTheSerialStream()
    {
        var memory = new PlcMemory();
        memory.SetWords("IR", 5, 0x0042);
        memory.SetBits("DI", 7, true);
        using var session = new SerialSession(new ModbusSlave(ModbusFraming.Rtu, memory));
        using ModbusClient client = session.Client(requestTimeout: 2000);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        Assert.True((await client.WriteUInt16Async("HR10", 0x1234)).IsSuccess);
        Assert.Equal((ushort)0x1234, (await client.ReadUInt16Async("HR10")).Data);
        Assert.Equal((ushort)0x0042, (await client.ReadUInt16Async("IR5")).Data);
        Assert.True((await client.WriteBoolAsync("C3", true)).IsSuccess);
        Assert.True((await client.ReadBoolAsync("C3")).Data);
        Assert.True((await client.ReadBoolAsync("DI7")).Data);
    }

    [Fact(Timeout = 60000)]
    public async Task NoiseBeforeTheReply_IsSkipped_OverTheSerialStream()
    {
        var memory = new PlcMemory();
        memory.SetWords("HR", 0, 0x0C0D);
        var slave = new ModbusSlave(ModbusFraming.Rtu, memory) { NoisePrefix = new byte[] { 0x10, 0x20, 0x30 } };
        using var session = new SerialSession(slave);
        using ModbusClient client = session.Client(requestTimeout: 2000);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        CommResult<ushort> result = await client.ReadUInt16Async("HR0");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal((ushort)0x0C0D, result.Data);
    }

    [Fact(Timeout = 60000)]
    public async Task StationPolling_OverTheSerialStream_TimesOutTheSilentStation()
    {
        var slave = new ModbusSlave(ModbusFraming.Rtu, new PlcMemory());
        slave.Units.Clear();
        slave.Units.Add(1);
        slave.Units.Add(2);
        using var session = new SerialSession(slave);
        using ModbusClient client = session.Client(requestTimeout: 300);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        Assert.True((await client.ReadUInt16Async("s=1;HR0")).IsSuccess);
        Assert.True((await client.ReadUInt16Async("s=2;HR0")).IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, (await client.ReadUInt16Async("s=3;HR0")).ErrorKind);
        Assert.True((await client.ReadUInt16Async("s=1;HR0")).IsSuccess);
    }

    [SerialPairFact(Timeout = 180000)]
    public async Task RtuReadAndWrite_OverARealSerialPair()
    {
        (string first, string second) = SerialPairEnvironment.Read();
        var options = new ModbusClientOptions { Framing = ModbusFraming.Rtu };
        var slave = new ModbusSlave(ModbusFraming.Rtu, new PlcMemory());
        using var devicePort = new SerialPort(second, 9600, Parity.None, 8, StopBits.One);
        devicePort.Open();
        using var stopDevice = new CancellationTokenSource();
        Task device = new DeviceSimulator(ModbusDeviceCodecs.For(ModbusFraming.Rtu), slave.Handle).RunAsync(devicePort.BaseStream, stopDevice.Token);

        using ModbusClient client = ModbusClient.Create(new SerialChannelConfig { PortName = first, BaudRate = 9600 }, options);
        try
        {
            Assert.True((await client.ConnectAsync()).IsSuccess);
            Assert.True((await client.WriteUInt16Async("HR5", 0xBEEF)).IsSuccess);
            Assert.Equal((ushort)0xBEEF, (await client.ReadUInt16Async("HR5")).Data);
            Assert.True((await client.WriteBoolAsync("C5", true)).IsSuccess);
            Assert.True((await client.ReadBoolAsync("C5")).Data);
        }
        finally
        {
            stopDevice.Cancel();
            await device.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>内存中的串口会话：模拟从站在管道的另一端运行，直到释放。</summary>
    private sealed class SerialSession : IDisposable
    {
        private readonly ModbusSlave slave;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private DeviceSimulator? simulator;

        public SerialSession(ModbusSlave slave)
        {
            this.slave = slave;
        }

        public ModbusClient Client(int requestTimeout)
        {
            var options = new ModbusClientOptions { Framing = ModbusFraming.Rtu };
            var settings = ModbusProtocolSettings.From(options);
            ChannelComponents components = ModbusProtocolProfile.Configure(new ChannelComponents(), settings, ChannelDelivery.Stream, ChannelMedium.Serial);
            simulator = new DeviceSimulator(ModbusDeviceCodecs.For(ModbusFraming.Rtu), slave.Handle);
            var channel = new DuplexClientChannel(new ClientChannelSettings { RequestTimeout = requestTimeout }, components, pair =>
            {
                _ = simulator.RunAsync(pair.B, stop.Token);
                return Task.CompletedTask;
            });
            return new ModbusClient(channel, settings, ModbusFraming.Rtu, options, null);
        }

        public void Dispose() => stop.Cancel();
    }
}
