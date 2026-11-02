using System.Diagnostics;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Testing;
using Xunit.Abstractions;
using static Junevy.Communication.Modbus.Tests.Interop.PymodbusScenarios;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 与 Modbus Slave 软件核对的场景（P7-D17 第 ① 项尚未核对的部分），TCP 与串口共用。
/// 从站的数据由手工帧读写（<see cref="ModbusToolWire"/>），因此读写两个方向都不依赖客户端的另一半实现。
/// </summary>
internal static class ModbusToolScenarios
{
    private const int ReadCoils = 0x01;
    private const int ReadDiscreteInputs = 0x02;
    private const int ReadInputRegisters = 0x04;
    private const int WriteSingleCoil = 0x05;
    private const int WriteSingleRegister = 0x06;
    private const int WriteMultipleCoils = 0x0F;
    private const int WriteMultipleRegisters = 0x10;
    private const int MaskWrite = 0x16;
    private const int ReadWriteMultiple = 0x17;

    private const int IllegalFunction = 0x01;
    private const string IllegalFunctionName = "Illegal Function";
    private const int IllegalDataAddress = 0x02;
    private const string IllegalDataAddressName = "Illegal Data Address";

    /// <summary>读：手工帧写入已知的内容，客户端的地址换算、字序与类型解码必须与之相符。</summary>
    public static async Task ReadsDecodeWhatTheWireHoldsAsync(ModbusToolWire wire)
    {
        // 0x12345678、3.25f、"Junevy"、short.MinValue、第 3 位、全 1。
        ushort[] layout = { 0x1234, 0x5678, 0x4050, 0x0000, 0x4A75, 0x6E65, 0x7679, 0x8000, 0x0008, 0xFFFF };
        wire.WriteHolding(0, layout);

        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions())))
        {
            Assert.Equal(layout, Ok(await client.ReadUInt16Async("HR0", ModbusToolWire.AreaSize)));
            Assert.Equal((ushort)0x1234, Ok(await client.ReadUInt16Async("40001")));
            Assert.Equal((ushort)0xFFFF, Ok(await client.ReadUInt16Async("40010")));
            Assert.Equal(0x12345678, Ok(await client.ReadInt32Async("HR0")));
            Assert.Equal(3.25f, Ok(await client.ReadFloatAsync("HR2")));
            Assert.Equal("Junevy", Ok(await client.ReadStringAsync("HR4", 6)));
            Assert.Equal(short.MinValue, Ok(await client.ReadInt16Async("HR7")));
            Assert.True(Ok(await client.ReadBoolAsync("HR8.3")));
            Assert.False(Ok(await client.ReadBoolAsync("HR8.2")));
        }

        using ModbusClient swapped = await ConnectAsync(wire.CreateClient(new ModbusClientOptions { DataFormat = new PlcDataFormat(WordOrder.CDAB) }));
        Assert.Equal(0x56781234, Ok(await swapped.ReadInt32Async("HR0")));
    }

    /// <summary>写：客户端写入，手工帧核对落点（地址没有偏移）与字节序，并核对实际发出的功能码。</summary>
    public static async Task WritesLandOnTheWireAsync(ModbusToolWire wire)
    {
        wire.WriteHolding(0, new ushort[ModbusToolWire.AreaSize]);

        var log = new TestLogger();
        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions(), loggerFactory: log)))
        {
            Ok(await client.WriteUInt16Async("HR0", 0xBEEF));
            Ok(await client.WriteUInt16Async("HR1", new ushort[] { 1, 2, 3 }));
            Ok(await client.WriteInt32Async("HR4", 0x12345678));
            Ok(await client.WriteFloatAsync("HR6", 3.25f));
            Ok(await client.WriteStringAsync("HR8", "Ju"));
            Ok(await client.WriteUInt16Async("40010", 0x0102));
        }

        Assert.Equal(new[] { WriteSingleRegister, WriteMultipleRegisters, WriteMultipleRegisters, WriteMultipleRegisters, WriteSingleRegister, WriteSingleRegister }, wire.SentFunctions(log));
        Assert.Equal(new ushort[] { 0xBEEF, 1, 2, 3, 0x1234, 0x5678, 0x4050, 0x0000, 0x4A75, 0x0102 }, wire.ReadHolding(0, ModbusToolWire.AreaSize));

        // 只用多写功能码的选项：单个字也走 0x10。
        var multipleLog = new TestLogger();
        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions { UseSingleWriteFunctions = false }, loggerFactory: multipleLog)))
            Ok(await client.WriteUInt16Async("HR0", 0x1111));

        Assert.Equal(new[] { WriteMultipleRegisters }, wire.SentFunctions(multipleLog));
        Assert.Equal((ushort)0x1111, wire.ReadHolding(0, 1)[0]);
    }

    /// <summary>0x16、0x17 与寄存器位写入。</summary>
    public static async Task MaskWriteAndReadWriteAsync(ModbusToolWire wire)
    {
        wire.WriteHolding(0, new ushort[ModbusToolWire.AreaSize]);
        wire.WriteHolding(0, 0x00F0);

        var log = new TestLogger();
        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions { UseMaskWriteForBits = true }, loggerFactory: log)))
        {
            // 结果 = (当前 AND and) OR (or AND NOT and)。
            Ok(await client.MaskWriteRegisterAsync("HR0", andMask: 0x00FF, orMask: 0x1234));

            // 0x17：先写后读，同一地址读到刚写入的值；不同地址时读写互不影响。
            Assert.Equal(new byte[] { 0x00, 0x09, 0x00, 0x0A }, Ok(await client.ReadWriteMultipleRegistersAsync("HR1", 2, "HR1", new byte[] { 0x00, 0x09, 0x00, 0x0A })));
            Assert.Equal(new byte[] { 0x00, 0x09, 0x00, 0x0A }, Ok(await client.ReadWriteMultipleRegistersAsync("HR1", 2, "HR3", new byte[] { 0x00, 0x07, 0x00, 0x08 })));

            Ok(await client.WriteBoolAsync("HR5.3", true));
            Assert.True(Ok(await client.ReadBoolAsync("HR5.3")));
        }

        Assert.Equal(new[] { MaskWrite, ReadWriteMultiple, ReadWriteMultiple, MaskWrite }, wire.SentFunctions(log).Where(function => function is MaskWrite or ReadWriteMultiple));
        Assert.Equal(new ushort[] { 0x12F0, 9, 10, 7, 8, 0x0008 }, wire.ReadHolding(0, 6));

        // 读改写：位写入改用 03 + 06，其他位不变。
        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions { AllowReadModifyWriteBits = true })))
        {
            Ok(await client.WriteBoolAsync("HR5.1", true));
            Ok(await client.WriteBoolAsync("HR5.3", false));
        }

        Assert.Equal((ushort)0x0002, wire.ReadHolding(5, 1)[0]);
    }

    /// <summary>从站的异常应答要解析成 <c>RemoteError</c>，之后链路照常可用。</summary>
    public static async Task ExceptionResponsesAsync(ModbusToolWire wire)
    {
        wire.WriteHolding(0, 0x0A0B);

        using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions { UseMaskWriteForBits = true }));

        AssertRemoteError(await client.ReadUInt16Async("HR10"), IllegalDataAddress, IllegalDataAddressName);
        AssertRemoteError(await client.ReadUInt16Async("HR9", 2), IllegalDataAddress, IllegalDataAddressName);
        AssertRemoteError(await client.ReadUInt16Async("HR0", 11), IllegalDataAddress, IllegalDataAddressName);
        AssertRemoteError(await client.WriteUInt16Async("HR10", 1), IllegalDataAddress, IllegalDataAddressName);
        AssertRemoteError(await client.WriteUInt16Async("HR9", new ushort[] { 1, 2 }), IllegalDataAddress, IllegalDataAddressName);
        AssertRemoteError(await client.ReadWriteMultipleRegistersAsync("HR10", 1, "HR10", new byte[] { 0, 1 }), IllegalDataAddress, IllegalDataAddressName);
        AssertRemoteError(await client.ReadUInt16Async("HR0", 126), IllegalDataAddress, IllegalDataAddressName);

        // Modbus Slave 对越界的 0x16 不回异常，而是原样回显（手工帧同样如此）；回显合规，客户端按成功上报。写入不会落到定义的寄存器上。
        ushort[] before = Ok(await client.ReadUInt16Async("HR0", ModbusToolWire.AreaSize));
        Ok(await client.MaskWriteRegisterAsync("HR10", 0x0000, 0xFFFF));
        Assert.Equal(before, Ok(await client.ReadUInt16Async("HR0", ModbusToolWire.AreaSize)));

        Assert.Equal((ushort)0x0A0B, Ok(await client.ReadUInt16Async("HR0")));
    }

    /// <summary>
    /// 线圈（01、05、0F），要在 Modbus Slave 里另开窗口定义（地址 0–9）；没有定义时从站回异常码 01。
    /// 读：手工帧写入不对称、跨字节的内容；写：客户端写，手工帧核对落点与实际发出的功能码；越界由从站拒绝，且不改动已定义的线圈。
    /// </summary>
    public static async Task CoilsAsync(ModbusToolWire wire, ITestOutputHelper output)
    {
        bool[]? held = wire.ReadBits(ReadCoils, 0, ModbusToolWire.AreaSize);
        output.WriteLine(held is null ? "Coils are not defined on the slave." : $"Coils 0-9 on the slave: {Format(held)}.");
        if (held is null)
        {
            using ModbusClient undefined = await ConnectAsync(wire.CreateClient(new ModbusClientOptions()));
            AssertRemoteError(await undefined.ReadBitsAsync("C0", 8), IllegalFunction, IllegalFunctionName);
            AssertRemoteError(await undefined.WriteBoolAsync("C0", true), IllegalFunction, IllegalFunctionName);
            AssertRemoteError(await undefined.WriteBitsAsync("C0", new[] { true, false }), IllegalFunction, IllegalFunctionName);
            Ok(await undefined.ReadUInt16Async("HR0"));
            return;
        }

        bool[] pattern = { true, false, false, true, true, false, true, false, false, true };
        wire.WriteCoils(0, pattern);

        var log = new TestLogger();
        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions(), loggerFactory: log)))
        {
            Assert.Equal(pattern, Ok(await client.ReadBitsAsync("C0", ModbusToolWire.AreaSize)));
            Assert.Equal(pattern.Skip(3).Take(6).ToArray(), Ok(await client.ReadBitsAsync("C3", 6)));
            Assert.Equal(pattern[9], Ok(await client.ReadBoolAsync("00010")));

            Ok(await client.WriteBitsAsync("C2", new[] { true, true, false, true }));
            Ok(await client.WriteBoolAsync("C9", false));
            Ok(await client.WriteBoolAsync("00001", false));
        }

        Assert.Equal(new[] { ReadCoils, ReadCoils, ReadCoils, WriteMultipleCoils, WriteSingleCoil, WriteSingleCoil }, wire.SentFunctions(log));
        bool[] written = { false, false, true, true, false, true, true, false, false, false };
        Assert.Equal(written, wire.ReadBits(ReadCoils, 0, ModbusToolWire.AreaSize));

        // 只用多写功能码的选项：单个线圈也走 0x0F。
        var multipleLog = new TestLogger();
        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions { UseSingleWriteFunctions = false }, loggerFactory: multipleLog)))
            Ok(await client.WriteBoolAsync("C7", true));

        Assert.Equal(new[] { WriteMultipleCoils }, wire.SentFunctions(multipleLog));
        written[7] = true;
        Assert.Equal(written, wire.ReadBits(ReadCoils, 0, ModbusToolWire.AreaSize));

        using (ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions())))
        {
            AssertRemoteError(await client.ReadBitsAsync("C10", 1), IllegalDataAddress, IllegalDataAddressName);
            AssertRemoteError(await client.ReadBitsAsync("C0", ModbusToolWire.AreaSize + 1), IllegalDataAddress, IllegalDataAddressName);
            AssertRemoteError(await client.WriteBoolAsync("C10", true), IllegalDataAddress, IllegalDataAddressName);
            AssertRemoteError(await client.WriteBitsAsync("C9", new[] { true, true }), IllegalDataAddress, IllegalDataAddressName);

            // 异常应答之后链路照常可用。
            Assert.Equal(written[9], Ok(await client.ReadBoolAsync("C9")));
        }

        Assert.Equal(written, wire.ReadBits(ReadCoils, 0, ModbusToolWire.AreaSize));
    }

    /// <summary>
    /// 离散输入（02），要在 Modbus Slave 里另开窗口定义（地址 0–9）；没有定义时从站回异常码 01。
    /// 客户端不能写，值由用户在 Modbus Slave 里设置，判据是手工帧读到的内容：整段（跨字节）、带偏移的一段、单点，以及越界。
    /// </summary>
    public static async Task DiscreteInputsAsync(ModbusToolWire wire, ITestOutputHelper output)
    {
        bool[]? held = wire.ReadBits(ReadDiscreteInputs, 0, ModbusToolWire.AreaSize);
        output.WriteLine(held is null ? "Discrete inputs are not defined on the slave." : $"Discrete inputs 0-9 on the slave: {Format(held)}.");

        using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions()));
        if (held is null)
        {
            AssertRemoteError(await client.ReadBitsAsync("DI0", 8), IllegalFunction, IllegalFunctionName);
        }
        else
        {
            // 全部相同的值发现不了地址偏移。
            Assert.True(held.Distinct().Count() > 1, "Set the discrete inputs 0-9 on the slave to a mix of 0 and 1.");

            Assert.Equal(held, Ok(await client.ReadBitsAsync("DI0", ModbusToolWire.AreaSize)));
            Assert.Equal(held.Skip(3).Take(6).ToArray(), Ok(await client.ReadBitsAsync("DI3", 6)));
            Assert.Equal(held[9], Ok(await client.ReadBoolAsync("10010")));
            AssertRemoteError(await client.ReadBitsAsync("DI10", 1), IllegalDataAddress, IllegalDataAddressName);
            AssertRemoteError(await client.ReadBitsAsync("DI0", ModbusToolWire.AreaSize + 1), IllegalDataAddress, IllegalDataAddressName);
        }

        // 异常应答之后链路照常可用。
        Ok(await client.ReadUInt16Async("HR0"));
    }

    /// <summary>
    /// 输入寄存器（04），要在 Modbus Slave 里另开窗口定义（地址 0–9）；没有定义时从站回异常码 01。
    /// 判据与离散输入相同，另加类型化读与寄存器位。
    /// </summary>
    public static async Task InputRegistersAsync(ModbusToolWire wire, ITestOutputHelper output)
    {
        ushort[]? held = wire.ReadWords(ReadInputRegisters, 0, ModbusToolWire.AreaSize);
        output.WriteLine(held is null ? "Input registers are not defined on the slave." : $"Input registers 0-9 on the slave: {string.Join(" ", held)}.");

        using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions()));
        if (held is null)
        {
            AssertRemoteError(await client.ReadUInt16Async("IR0", 8), IllegalFunction, IllegalFunctionName);
        }
        else
        {
            // 全部相同的值发现不了地址偏移。
            Assert.True(held.Distinct().Count() > 1, "Set the input registers 0-9 on the slave to different values.");

            Assert.Equal(held, Ok(await client.ReadUInt16Async("IR0", ModbusToolWire.AreaSize)));
            Assert.Equal(held.Skip(3).Take(6).ToArray(), Ok(await client.ReadUInt16Async("IR3", 6)));
            Assert.Equal(held[9], Ok(await client.ReadUInt16Async("30010")));
            Assert.Equal((int)(((uint)held[0] << 16) | held[1]), Ok(await client.ReadInt32Async("IR0")));
            Assert.Equal((held[1] & 0x0002) != 0, Ok(await client.ReadBoolAsync("IR1.1")));
            AssertRemoteError(await client.ReadUInt16Async("IR10"), IllegalDataAddress, IllegalDataAddressName);
            AssertRemoteError(await client.ReadUInt16Async("IR0", ModbusToolWire.AreaSize + 1), IllegalDataAddress, IllegalDataAddressName);
        }

        // 异常应答之后链路照常可用。
        Ok(await client.ReadUInt16Async("HR0"));
    }

    /// <summary>TCP 上站号的表现（⑤ 的一个样本）：从站是否按站号过滤是它自己的配置，这里只要求客户端能解析，之后默认站号照常。</summary>
    public static async Task TcpStationsAsync(ModbusToolWire wire, ITestOutputHelper output)
    {
        wire.WriteHolding(0, 0x0A0B);

        using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions(), new ModbusToolChannelSettings { RequestTimeout = 500 }));
        foreach (int station in new[] { 0, 2, 247, 255 })
        {
            CommResult<ushort> result = await client.ReadUInt16Async($"s={station};HR0");
            output.WriteLine($"Station {station}: {(result.IsSuccess ? $"0x{result.Data:X4}" : $"{result.ErrorKind} {result.ProtocolErrorCode}")}");

            Assert.True(result.IsSuccess || result.ErrorKind is CommErrorKind.RemoteError or CommErrorKind.Timeout, $"{result.ErrorKind}: {result.ErrorMessage}");
            Assert.Equal((ushort)0x0A0B, Ok(await client.ReadUInt16Async("HR0")));
        }
    }

    /// <summary>RTU 总线：别的站号不应答（超时），下一个请求不受影响；0 与 248 以上的站号不发送。</summary>
    public static async Task SerialStationsAsync(ModbusToolWire wire)
    {
        wire.WriteHolding(0, 0x0A0B);

        var log = new TestLogger();
        using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions(), new ModbusToolChannelSettings { RequestTimeout = 300 }, log));

        Assert.Equal(CommErrorKind.Timeout, (await client.ReadUInt16Async("s=2;HR0")).ErrorKind);
        Assert.Equal((ushort)0x0A0B, Ok(await client.ReadUInt16Async("HR0")));
        Assert.Equal(CommErrorKind.Timeout, (await client.ReadUInt16Async("s=247;HR0")).ErrorKind);
        Assert.Equal((ushort)0x0A0B, Ok(await client.ReadUInt16Async("s=1;HR0")));

        int sent = wire.SentFunctions(log).Length;
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.ReadUInt16Async("s=0;HR0")).ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.ReadUInt16Async("s=248;HR0")).ErrorKind);
        Assert.Equal(sent, wire.SentFunctions(log).Length);
    }

    /// <summary>连续与并发：每个应答都必须对应自己的请求（值按寄存器区分），不允许一次失败。</summary>
    public static async Task SoakAndConcurrencyAsync(ModbusToolWire wire, int sequentialReads, int callers, int readsPerCaller, int writesPerRegister, ITestOutputHelper output)
    {
        ushort[] values = Enumerable.Range(0, ModbusToolWire.AreaSize).Select(i => (ushort)(0x1000 + i)).ToArray();
        wire.WriteHolding(0, values);

        using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions()));

        long slowest = 0;
        Stopwatch total = Stopwatch.StartNew();
        for (int i = 0; i < sequentialReads; i++)
        {
            long started = total.ElapsedMilliseconds;
            int register = i % ModbusToolWire.AreaSize;
            Assert.Equal(values[register], Ok(await client.ReadUInt16Async($"HR{register}")));
            slowest = Math.Max(slowest, total.ElapsedMilliseconds - started);
        }

        output.WriteLine($"{sequentialReads} sequential reads: {total.ElapsedMilliseconds} ms, slowest {slowest} ms.");

        total.Restart();
        await Task.WhenAll(Enumerable.Range(0, callers).Select(async caller =>
        {
            for (int i = 0; i < readsPerCaller; i++)
            {
                int register = (caller + i) % ModbusToolWire.AreaSize;
                Assert.Equal(values[register], Ok(await client.ReadUInt16Async($"HR{register}")));
            }
        }));
        output.WriteLine($"{callers} callers x {readsPerCaller} reads: {total.ElapsedMilliseconds} ms.");

        total.Restart();
        await Task.WhenAll(Enumerable.Range(0, ModbusToolWire.AreaSize).Select(async register =>
        {
            for (int i = 0; i < writesPerRegister; i++)
            {
                ushort value = (ushort)((register << 12) | i);
                Ok(await client.WriteUInt16Async($"HR{register}", value));
                Assert.Equal(value, Ok(await client.ReadUInt16Async($"HR{register}")));
            }
        }));
        output.WriteLine($"{ModbusToolWire.AreaSize} registers x {writesPerRegister} write + read: {total.ElapsedMilliseconds} ms.");
    }

    /// <summary>同时多条 TCP 连接（从站是否接受多个主站）。</summary>
    public static async Task SeveralClientsAsync(ModbusToolWire wire, int clients, int readsPerClient)
    {
        wire.WriteHolding(0, 0x0A0B);

        await Task.WhenAll(Enumerable.Range(0, clients).Select(async _ =>
        {
            using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions()));
            for (int i = 0; i < readsPerClient; i++)
                Assert.Equal((ushort)0x0A0B, Ok(await client.ReadUInt16Async("HR0")));
        }));
    }

    /// <summary>反复连接、断开、释放：同一实例可以再连，新实例能重新打开连接或串口。</summary>
    public static async Task ConnectCyclesAsync(ModbusToolWire wire, int cycles)
    {
        wire.WriteHolding(0, 0x0C0D);

        using (ModbusClient client = wire.CreateClient(new ModbusClientOptions()))
        {
            for (int i = 0; i < cycles; i++)
            {
                Ok(await client.ConnectAsync());
                Assert.Equal((ushort)0x0C0D, Ok(await client.ReadUInt16Async("HR0")));
                await client.DisconnectAsync();
                Assert.Equal(ConnectionState.Disconnected, client.State);
            }
        }

        for (int i = 0; i < cycles; i++)
        {
            using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions()));
            Assert.Equal((ushort)0x0C0D, Ok(await client.ReadUInt16Async("HR0")));
        }
    }

    /// <summary>心跳探测（读 HR0）被从站接受：空闲时持续发送，链路不离开 Connected。</summary>
    public static async Task HeartbeatStaysHealthyAsync(ModbusToolWire wire)
    {
        const int idleMs = 2000;
        const int minimumProbes = 5;
        var settings = new ModbusToolChannelSettings
        {
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 1000, MaxFailures = 2 },
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 100 },
        };

        var log = new TestLogger();
        using ModbusClient client = await ConnectAsync(wire.CreateClient(new ModbusClientOptions(), settings, log));
        int left = 0;
        client.StateChanged += (_, _) => Interlocked.Increment(ref left);

        await Task.Delay(idleMs);

        Assert.Equal(0, Volatile.Read(ref left));
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.True(wire.SentFunctions(log).Length >= minimumProbes, $"Only {wire.SentFunctions(log).Length} probes in {idleMs} ms.");
        Ok(await client.ReadUInt16Async("HR0"));
    }

    /// <summary>链路故障（经中继制造，从站看到真实的断连）：断开、黑洞、迟到应答之后，客户端恢复且读到的值正确。</summary>
    public static async Task TcpLinkFaultsAsync(ModbusToolTcpWire wire, ITestOutputHelper output)
    {
        const int requestTimeout = 500;
        const int recoveryMs = 15000;
        ushort[] values = Enumerable.Range(0, ModbusToolWire.AreaSize).Select(i => (ushort)(0x2000 + i)).ToArray();
        wire.WriteHolding(0, values);

        await using var relay = new TcpFaultRelay(wire.Host, wire.Port);
        var settings = new ModbusToolChannelSettings
        {
            RequestTimeout = requestTimeout,
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 200, Timeout = 300, MaxFailures = 2 },
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 100, MaxInterval = 500 },
        };
        using (ModbusClient client = await ConnectAsync(ModbusToolTcpWire.CreateClient("127.0.0.1", relay.Port, new ModbusClientOptions(), settings)))
        {
            Assert.Equal(values[1], Ok(await client.ReadUInt16Async("HR1")));

            // 断开：心跳或读循环发现后重连，新连接由从站接受。
            relay.DropConnections();
            await Polling.WaitUntilAsync(() => relay.Connections >= 2, recoveryMs);
            await ReadUntilRecoveredAsync(client, "HR2", values[2], recoveryMs);
            output.WriteLine($"After the drop: {relay.Connections} connections.");

            // 黑洞：连接还在但没有应答。请求超时，心跳判定失效，恢复后读到正确的值。
            relay.Blackhole = true;
            Assert.False((await client.ReadUInt16Async("HR3")).IsSuccess);
            await Polling.WaitUntilAsync(() => client.State != ConnectionState.Connected, recoveryMs);
            relay.Blackhole = false;
            await ReadUntilRecoveredAsync(client, "HR4", values[4], recoveryMs);
            output.WriteLine($"After the blackhole: {relay.Connections} connections.");
        }

        // 迟到的应答：不开心跳，连接保持。旧应答恰好在下一个请求等待期间到达，事务号不符，不能被当成这个请求的应答。
        using ModbusClient plain = await ConnectAsync(ModbusToolTcpWire.CreateClient("127.0.0.1", relay.Port, new ModbusClientOptions(), new ModbusToolChannelSettings { RequestTimeout = requestTimeout }));
        int connections = relay.Connections;
        Assert.Equal(values[0], Ok(await plain.ReadUInt16Async("HR0")));

        relay.ReplyDelay = requestTimeout * 3;
        Assert.Equal(CommErrorKind.Timeout, (await plain.ReadUInt16Async("HR5")).ErrorKind);
        relay.ReplyDelay = 0;
        await Task.Delay(requestTimeout * 3 / 2);

        Assert.Equal(values[6], Ok(await plain.ReadUInt16Async("HR6")));
        for (int i = 0; i < 50; i++)
        {
            int register = i % ModbusToolWire.AreaSize;
            Assert.Equal(values[register], Ok(await plain.ReadUInt16Async($"HR{register}")));
        }

        Assert.Equal(connections, relay.Connections);
    }

    private static async Task ReadUntilRecoveredAsync(ModbusClient client, string address, ushort expected, int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        CommResult<ushort> result;
        do
        {
            result = await client.ReadUInt16Async(address);
            if (result.IsSuccess)
            {
                Assert.Equal(expected, result.Data);
                return;
            }

            await Task.Delay(50);
        }
        while (elapsed.ElapsedMilliseconds < milliseconds);

        Assert.Fail($"Not recovered within {milliseconds} ms: {result.ErrorKind} {result.ErrorMessage}");
    }

    private static string Format(bool[] bits) => string.Concat(bits.Select(bit => bit ? '1' : '0'));
}
