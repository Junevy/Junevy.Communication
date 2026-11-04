using System.Diagnostics;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Testing;
using Xunit.Abstractions;
using static Junevy.Communication.Siemens.Tests.Interop.S7ToolWire;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests.Interop;

/// <summary>
/// 与第三方 S7 服务器核对的场景（README "Not Yet Verified" 一节）。服务器的数据由手工帧读写（<see cref="S7ToolWire"/>），
/// 因此读写两个方向都不依赖客户端的另一半实现。
/// </summary>
internal static class S7ToolScenarios
{
    private const int SetupFunction = 0xF0;
    private const int ReadFunction = 0x04;
    private const int WriteFunction = 0x05;

    private const int ObjectDoesNotExist = 0x0A;
    private const int InvalidAddress = 0x05;

    /// <summary>
    /// DB1 开头的已知布局：0 Int16 -1234、2 Int32 0x12345678、6 Real 3.25、10 LReal 1.5、18 LInt 0x0102030405060708、
    /// 26 Word 0xBEEF、28 DWord 0xDEADBEEF、32 位 32.3、33 位 33.0 与 33.7、34 字符 "Junevy"、
    /// 40 String[10] "Siemen"、52 WString[4] "S7中"。
    /// </summary>
    private static readonly byte[] Layout = ToBytes(
        "FB 2E 12 34 56 78 40 50 00 00 3F F8 00 00 00 00 00 00 01 02 03 04 05 06 07 08 BE EF DE AD BE EF " +
        "08 81 4A 75 6E 65 76 79 0A 06 53 69 65 6D 65 6E 00 00 00 00 00 04 00 03 00 53 00 37 4E 2D 00 00");

    /// <summary>握手：协商值与手工帧一致；三种连接类型与常用槽号都被接受。</summary>
    public static async Task HandshakeAsync(S7ToolWire wire, ITestOutputHelper output)
    {
        wire.Write(DataBlock, 1, 0, new byte[] { 0x0A, 0x0B });

        var log = new TestLogger();
        await using (S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), loggerFactory: log)))
        {
            output.WriteLine($"Client negotiated {client.NegotiatedPduSize}, hand frame (asked 480) negotiated {wire.NegotiatedPdu}.");
            Assert.Equal(wire.NegotiatedPdu, client.NegotiatedPduSize);
            Assert.Equal((ushort)0x0A0B, Succeeded(await client.ReadUInt16Async("DB1.DBW0")));
        }

        // Setup Communication 在第一帧，请求 960（参数 F0 00 00 01 00 01 03 C0）。
        byte[] setup = SentJobs(log)[0];
        Assert.Equal(SetupFunction, FunctionOf(setup));
        Assert.Equal(960, (setup[23] << 8) | setup[24]);

        foreach (S7ConnectionType type in new[] { S7ConnectionType.PG, S7ConnectionType.OP, S7ConnectionType.Basic })
        {
            foreach (int slot in new[] { 0, 1, 2 })
            {
                await using S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions { ConnectionType = type, Rack = 0, Slot = slot }));
                Assert.Equal((ushort)0x0A0B, Succeeded(await client.ReadUInt16Async("DB1.DBW0")));
            }
        }
    }

    /// <summary>读：手工帧写入已知布局，客户端的地址换算、字序、位地址与类型解码必须与之相符。</summary>
    public static async Task ReadsDecodeWhatTheWireHoldsAsync(S7ToolWire wire)
    {
        wire.Write(DataBlock, 1, 0, Layout);
        wire.Write(DataBlock, 2, 10, new byte[] { 0x44 });
        wire.Write(Merkers, 0, 10, new byte[] { 0x33, 0x34 });
        wire.Write(Inputs, 0, 10, new byte[] { 0x11 });
        wire.Write(Outputs, 0, 10, new byte[] { 0x22 });

        await using (S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions())))
        {
            Assert.Equal(Layout, Succeeded(await client.ReadBytesAsync("DB1.DBB0", Layout.Length)));
            Assert.Equal(Layout.Skip(3).Take(5).ToArray(), Succeeded(await client.ReadBytesAsync("DB1.DBB3", 5)));
            Assert.Equal((short)-1234, Succeeded(await client.ReadInt16Async("DB1.DBW0")));
            Assert.Equal((short)-1234, Succeeded(await client.ReadInt16Async("%db1.dbw0")));
            Assert.Equal(0x12345678, Succeeded(await client.ReadInt32Async("DB1.DBD2")));
            Assert.Equal(3.25f, Succeeded(await client.ReadFloatAsync("DB1.DBD6")));
            Assert.Equal(1.5, Succeeded(await client.ReadDoubleAsync("DB1.DBD10")));
            Assert.Equal(0x0102030405060708L, Succeeded(await client.ReadInt64Async("DB1.DBD18")));
            Assert.Equal((ushort)0xBEEF, Succeeded(await client.ReadUInt16Async("DB1.DBW26")));
            Assert.Equal(0xDEADBEEFu, Succeeded(await client.ReadUInt32Async("DB1.DBD28")));
            Assert.Equal(new short[] { -1234, 0x1234, 0x5678 }, Succeeded(await client.ReadInt16Async("DB1.DBW0", 3)));

            Assert.True(Succeeded(await client.ReadBoolAsync("DB1.DBX32.3")));
            Assert.False(Succeeded(await client.ReadBoolAsync("DB1.DBX32.2")));
            Assert.True(Succeeded(await client.ReadBoolAsync("DB1.DBX33.7")));
            Assert.Equal(new[] { false, false, true, false }, Succeeded(await client.ReadBitsAsync("DB1.DBX32.6", 4)));

            Assert.Equal("Junevy", Succeeded(await client.ReadStringAsync("DB1.DBB34", 6)));
            Assert.Equal("Siemen", Succeeded(await client.ReadS7StringAsync("DB1.DBB40", 10)));
            Assert.Equal("S7中", Succeeded(await client.ReadS7WStringAsync("DB1.DBB52", 4)));

            // 区域与 DB 号：同一字节号在每个区域读到的是各自的值。
            Assert.Equal((byte)0x44, Succeeded(await client.ReadByteAsync("DB2.DBB10")));
            Assert.Equal((byte)0x33, Succeeded(await client.ReadByteAsync("MB10")));
            Assert.Equal((ushort)0x3334, Succeeded(await client.ReadUInt16Async("MW10")));
            Assert.Equal((byte)0x11, Succeeded(await client.ReadByteAsync("IB10")));
            Assert.Equal((byte)0x22, Succeeded(await client.ReadByteAsync("QB10")));
            Assert.Equal(Layout[10], Succeeded(await client.ReadByteAsync("DB1.DBB10")));
            Assert.True(Succeeded(await client.ReadBoolAsync("M10.0")));
            Assert.True(Succeeded(await client.ReadBoolAsync("I10.4")));
            Assert.True(Succeeded(await client.ReadBoolAsync("Q10.1")));
            Assert.False(Succeeded(await client.ReadBoolAsync("Q10.0")));
        }

        await using S7Client swapped = await ConnectAsync(wire.CreateClient(new S7ClientOptions { DataFormat = new PlcDataFormat(WordOrder.CDAB) }));
        Assert.Equal(0x56781234, Succeeded(await swapped.ReadInt32Async("DB1.DBD2")));
    }

    /// <summary>写：客户端写入，手工帧核对落点（字节号没有偏移、相邻字节不动）与字节序。</summary>
    public static async Task WritesLandOnTheWireAsync(S7ToolWire wire)
    {
        wire.Write(DataBlock, 1, 0, new byte[Layout.Length]);
        wire.Write(DataBlock, 1, 100, Enumerable.Repeat((byte)0xEE, 20).ToArray());
        wire.Write(DataBlock, 2, 10, new byte[1]);
        wire.Write(Merkers, 0, 10, new byte[2]);
        wire.Write(Inputs, 0, 10, new byte[1]);
        wire.Write(Outputs, 0, 10, new byte[1]);

        var log = new TestLogger();
        await using (S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), loggerFactory: log)))
        {
            Succeeded(await client.WriteInt16Async("DB1.DBW0", -1234));
            Succeeded(await client.WriteInt32Async("DB1.DBD2", 0x12345678));
            Succeeded(await client.WriteFloatAsync("DB1.DBD6", 3.25f));
            Succeeded(await client.WriteDoubleAsync("DB1.DBD10", 1.5));
            Succeeded(await client.WriteInt64Async("DB1.DBD18", 0x0102030405060708L));
            Succeeded(await client.WriteUInt16Async("DB1.DBW26", 0xBEEF));
            Succeeded(await client.WriteUInt32Async("DB1.DBD28", 0xDEADBEEFu));
            Succeeded(await client.WriteBoolAsync("DB1.DBX32.3", true));
            Succeeded(await client.WriteBoolAsync("DB1.DBX33.0", true));
            Succeeded(await client.WriteBoolAsync("DB1.DBX33.7", true));
            Succeeded(await client.WriteStringAsync("DB1.DBB34", "Junevy"));
            Succeeded(await client.WriteS7StringAsync("DB1.DBB40", "Siemen", 10));
            Succeeded(await client.WriteS7WStringAsync("DB1.DBB52", "S7中", 4));

            Succeeded(await client.WriteBytesAsync("DB1.DBB103", ToBytes("01 02 03 04 05 06 07")));

            Succeeded(await client.WriteByteAsync("DB2.DBB10", 0x44));
            Succeeded(await client.WriteUInt16Async("MW10", 0x3334));
            Succeeded(await client.WriteByteAsync("IB10", 0x11));
            Succeeded(await client.WriteByteAsync("QB10", 0x22));
        }

        Assert.Equal(Layout, wire.Read(DataBlock, 1, 0, Layout.Length));
        Assert.Equal(ToBytes("EE EE EE 01 02 03 04 05 06 07 EE EE EE EE EE EE EE EE EE EE"), wire.Read(DataBlock, 1, 100, 20));
        Assert.Equal(new byte[] { 0x44 }, wire.Read(DataBlock, 2, 10, 1));
        Assert.Equal(new byte[] { 0x33, 0x34 }, wire.Read(Merkers, 0, 10, 2));
        Assert.Equal(new byte[] { 0x11 }, wire.Read(Inputs, 0, 10, 1));
        Assert.Equal(new byte[] { 0x22 }, wire.Read(Outputs, 0, 10, 1));

        // DB1.DBD10 的 LReal 跨过 DB1.DBB10，DB2 与 M、I、Q 区的写入没有落到 DB1。
        Assert.Equal((byte)0x3F, wire.Read(DataBlock, 1, 10, 1)[0]);

        // 每次写一个请求，全部是 Write Var（位写入也是原生的，不先读）。
        Assert.All(SentJobs(log).Skip(1), job => Assert.Equal(WriteFunction, FunctionOf(job)));
    }

    /// <summary>位：原生位写入只改目标位，跨字节的位读写按位地址落点；每个位一个请求。</summary>
    public static async Task BitsAsync(S7ToolWire wire)
    {
        wire.Write(DataBlock, 1, 20, new byte[] { 0xFF, 0x00, 0x00 });
        wire.Write(Merkers, 0, 5, new byte[2]);
        wire.Write(Inputs, 0, 2, new byte[1]);
        wire.Write(Outputs, 0, 3, new byte[1]);

        var log = new TestLogger();
        await using (S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), loggerFactory: log)))
        {
            Succeeded(await client.WriteBoolAsync("DB1.DBX20.2", false));
            int before = SentJobs(log).Length;
            Succeeded(await client.WriteBitsAsync("DB1.DBX21.6", new[] { true, false, true, true }));
            Assert.Equal(4, SentJobs(log).Length - before);

            Succeeded(await client.WriteBoolAsync("M5.7", true));
            Succeeded(await client.WriteBoolAsync("I2.1", true));
            Succeeded(await client.WriteBoolAsync("Q3.0", true));

            Assert.Equal(new[] { false, true, false }, Succeeded(await client.ReadBitsAsync("M5.6", 3)));
            Assert.Equal(new[] { true, false, true, true }, Succeeded(await client.ReadBitsAsync("DB1.DBX21.6", 4)));
        }

        Assert.Equal(new byte[] { 0xFB, 0x40, 0x03 }, wire.Read(DataBlock, 1, 20, 3));
        Assert.Equal(new byte[] { 0x80 }, wire.Read(Merkers, 0, 5, 1));
        Assert.Equal(new byte[] { 0x02 }, wire.Read(Inputs, 0, 2, 1));
        Assert.Equal(new byte[] { 0x01 }, wire.Read(Outputs, 0, 3, 1));
    }

    /// <summary>S7 的 String 与 WString：写只写头与实际字符（其余字节不动），读按头的实际长度解码；非法内容不发送或报 ProtocolViolation。</summary>
    public static async Task S7StringsAsync(S7ToolWire wire)
    {
        wire.Write(DataBlock, 1, 200, Enumerable.Repeat((byte)0xEE, 40).ToArray());
        wire.Write(DataBlock, 1, 240, ToBytes("0A 0C 41 42 43 44 45 46 47 48 49 4A 4B 4C"));

        var log = new TestLogger();
        await using S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), loggerFactory: log));

        Succeeded(await client.WriteS7StringAsync("DB1.DBB200", "Hello", 20));
        Succeeded(await client.WriteS7WStringAsync("DB1.DBB224", "Ω7", 6));
        Assert.Equal(ToBytes("14 05 48 65 6C 6C 6F EE"), wire.Read(DataBlock, 1, 200, 8));
        Assert.Equal(ToBytes("00 06 00 02 03 A9 00 37 EE"), wire.Read(DataBlock, 1, 224, 9));
        Assert.Equal("Hello", Succeeded(await client.ReadS7StringAsync("DB1.DBB200", 20)));
        Assert.Equal("Ω7", Succeeded(await client.ReadS7WStringAsync("DB1.DBB224", 6)));

        // 头里的实际长度 12 超过声明的最大长度 10。
        AssertViolation(await client.ReadS7StringAsync("DB1.DBB240", 10));

        int sent = SentJobs(log).Length;
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.WriteS7StringAsync("DB1.DBB200", "TooLongValue", 5)).ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.WriteS7StringAsync("DB1.DBB200", "x", 255)).ErrorKind);
        Assert.Equal(sent, SentJobs(log).Length);
        Assert.Equal(ToBytes("14 05 48 65 6C 6C 6F"), wire.Read(DataBlock, 1, 200, 7));
    }

    /// <summary>拆包：按协商的 PDU 计算每包上限（P4-D12），整段内容不错位；每个作业帧都不超过 PDU。</summary>
    public static async Task PacketSplittingAsync(S7ToolWire wire, ITestOutputHelper output)
    {
        const int length = 3000;
        const int start = 1000;
        byte[] pattern = Enumerable.Range(0, length).Select(i => (byte)(((i * 7) + 3) % 251)).ToArray();
        byte[] reversed = Enumerable.Reverse(pattern).ToArray();

        var log = new TestLogger();
        await using (S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), loggerFactory: log)))
        {
            int pdu = client.NegotiatedPduSize!.Value;
            int readLimit = pdu - 18;
            int writeLimit = pdu - 28;
            output.WriteLine($"PDU {pdu}: {readLimit} bytes per read, {writeLimit} bytes per write.");

            Succeeded(await client.WriteBytesAsync($"DB1.DBB{start}", pattern));
            Assert.Equal(pattern, wire.Read(DataBlock, 1, start, length));
            Assert.Equal(Packets(length, writeLimit), CountSince(log, 0, WriteFunction));

            wire.Write(DataBlock, 1, start, reversed);
            int mark = SentJobs(log).Length;
            Assert.Equal(reversed, Succeeded(await client.ReadBytesAsync($"DB1.DBB{start}", length)));
            Assert.Equal(Packets(length, readLimit), CountSince(log, mark, ReadFunction));

            // 边界：恰好一包与多一个字节。
            foreach ((int count, int function, int expected) in new[] { (readLimit, ReadFunction, 1), (readLimit + 1, ReadFunction, 2), (writeLimit, WriteFunction, 1), (writeLimit + 1, WriteFunction, 2) })
            {
                mark = SentJobs(log).Length;
                if (function == ReadFunction)
                    Assert.Equal(reversed.Take(count).ToArray(), Succeeded(await client.ReadBytesAsync($"DB1.DBB{start}", count)));
                else
                    Succeeded(await client.WriteBytesAsync($"DB1.DBB{start}", pattern.Take(count).ToArray()));

                Assert.Equal(expected, CountSince(log, mark, function));
            }

            Assert.Equal(pattern.Take(writeLimit + 1).ToArray(), wire.Read(DataBlock, 1, start, writeLimit + 1));

            // 类型数组同样拆包：150 个 Int16 = 300 字节。
            short[] words = Enumerable.Range(0, 150).Select(i => (short)((i * 331) - 20000)).ToArray();
            Succeeded(await client.WriteInt16Async($"DB1.DBW{start}", words));
            Assert.Equal(words, Succeeded(await client.ReadInt16Async($"DB1.DBW{start}", words.Length)));

            // 每个作业帧的 S7 部分都不超过 PDU，最大的写帧恰好等于 PDU（PDU − 28 的推导，README P4-D20 第 2 条）。
            int[] sizes = SentJobs(log).Select(job => job.Length - 7).ToArray();
            Assert.All(sizes, size => Assert.True(size <= pdu, $"A job of {size} bytes exceeds the PDU {pdu}."));
            Assert.Equal(pdu, sizes.Max());

            // 服务器对超出 PDU 的帧怎么回答：只记录，不同的服务器做法不同。
            output.WriteLine($"Hand-built write of {writeLimit + 1} bytes (S7 part {pdu + 1}): {Describe(() => wire.WriteReturnCode(DataBlock, 1, start, new byte[writeLimit + 1]))}.");
            output.WriteLine($"Hand-built read of {readLimit + 1} bytes (reply {pdu + 1}): {Describe(() => wire.ReadReturnCode(DataBlock, 1, start, readLimit + 1))}.");
        }
    }

    /// <summary>设备侧错误：返回码与手工帧看到的一致、消息按码表，之后链路照常可用。</summary>
    public static async Task RemoteErrorsAsync(S7ToolWire wire)
    {
        wire.Write(DataBlock, 1, 0, new byte[] { 0x0C, 0x0D });

        Assert.Equal(ObjectDoesNotExist, wire.ReadReturnCode(DataBlock, 100, 0, 4));
        Assert.Equal(ObjectDoesNotExist, wire.WriteReturnCode(DataBlock, 100, 0, new byte[2]));
        Assert.Equal(InvalidAddress, wire.ReadReturnCode(DataBlock, 1, 65530, 10));
        Assert.Equal(InvalidAddress, wire.ReadReturnCode(Merkers, 0, 70000, 10));
        Assert.Equal(Success, wire.ReadReturnCode(DataBlock, 1, 65300, 222));

        string doesNotExist = $"S7 item return code 0x0A (object does not exist). {PutGetHint}";
        string invalidAddress = $"S7 item return code 0x05 (invalid address). {PutGetHint}";

        await using S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions()));
        AssertRemoteError(await client.ReadBytesAsync("DB100.DBB0", 4), ObjectDoesNotExist, doesNotExist);
        AssertRemoteError(await client.WriteInt16Async("DB100.DBW0", 1), ObjectDoesNotExist, doesNotExist);
        AssertRemoteError(await client.ReadBoolAsync("DB100.DBX0.0"), ObjectDoesNotExist, doesNotExist);
        AssertRemoteError(await client.ReadBytesAsync("DB1.DBB65530", 10), InvalidAddress, invalidAddress);
        AssertRemoteError(await client.ReadBytesAsync("MB70000", 10), InvalidAddress, invalidAddress);

        // 拆成两包时，第二包越界：整个操作失败。
        AssertRemoteError(await client.ReadBytesAsync("DB1.DBB65300", 300), InvalidAddress, invalidAddress);

        // 地址本身非法：不发送。
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.ReadInt16Async("DB0.DBW0")).ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.ReadInt16Async("MX0.0")).ErrorKind);

        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Equal((ushort)0x0C0D, Succeeded(await client.ReadUInt16Async("DB1.DBW0")));
    }

    /// <summary>连续与并发：每个应答都必须对应自己的请求（值按地址区分），不允许一次失败。</summary>
    public static async Task SoakAndConcurrencyAsync(S7ToolWire wire, int sequentialReads, int callers, int readsPerCaller, int writesPerWord, ITestOutputHelper output)
    {
        const int words = 16;
        ushort[] values = Enumerable.Range(0, words).Select(i => (ushort)(0x1000 + i)).ToArray();
        wire.Write(DataBlock, 1, 0, values.SelectMany(value => new[] { (byte)(value >> 8), (byte)value }).ToArray());

        await using S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions()));

        long slowest = 0;
        Stopwatch total = Stopwatch.StartNew();
        for (int i = 0; i < sequentialReads; i++)
        {
            long started = total.ElapsedMilliseconds;
            int word = i % words;
            Assert.Equal(values[word], Succeeded(await client.ReadUInt16Async($"DB1.DBW{word * 2}")));
            slowest = Math.Max(slowest, total.ElapsedMilliseconds - started);
        }

        output.WriteLine($"{sequentialReads} sequential reads: {total.ElapsedMilliseconds} ms, slowest {slowest} ms.");

        total.Restart();
        await Task.WhenAll(Enumerable.Range(0, callers).Select(async caller =>
        {
            for (int i = 0; i < readsPerCaller; i++)
            {
                int word = (caller + i) % words;
                Assert.Equal(values[word], Succeeded(await client.ReadUInt16Async($"DB1.DBW{word * 2}")));
            }
        }));
        output.WriteLine($"{callers} callers x {readsPerCaller} reads: {total.ElapsedMilliseconds} ms.");

        total.Restart();
        await Task.WhenAll(Enumerable.Range(0, words).Select(async word =>
        {
            for (int i = 0; i < writesPerWord; i++)
            {
                ushort value = (ushort)((word << 12) | i);
                Succeeded(await client.WriteUInt16Async($"DB1.DBW{word * 2}", value));
                Assert.Equal(value, Succeeded(await client.ReadUInt16Async($"DB1.DBW{word * 2}")));
            }
        }));
        output.WriteLine($"{words} words x {writesPerWord} write + read: {total.ElapsedMilliseconds} ms.");

        ushort[] last = Enumerable.Range(0, words).Select(word => (ushort)((word << 12) | (writesPerWord - 1))).ToArray();
        byte[] held = wire.Read(DataBlock, 1, 0, words * 2);
        Assert.Equal(last, Enumerable.Range(0, words).Select(word => (ushort)((held[word * 2] << 8) | held[(word * 2) + 1])).ToArray());
    }

    /// <summary>同时多条连接（服务器是否接受多个客户端）。</summary>
    public static async Task SeveralClientsAsync(S7ToolWire wire, int clients, int readsPerClient)
    {
        wire.Write(DataBlock, 1, 0, new byte[] { 0x0A, 0x0B });

        await Task.WhenAll(Enumerable.Range(0, clients).Select(async _ =>
        {
            await using S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions()));
            for (int i = 0; i < readsPerClient; i++)
                Assert.Equal((ushort)0x0A0B, Succeeded(await client.ReadUInt16Async("DB1.DBW0")));
        }));
    }

    /// <summary>反复连接、断开、释放：同一实例可以再连（每次重新握手），新实例能重新打开连接。</summary>
    public static async Task ConnectCyclesAsync(S7ToolWire wire, int cycles)
    {
        wire.Write(DataBlock, 1, 0, new byte[] { 0x0C, 0x0D });

        var log = new TestLogger();
        await using (S7Client client = wire.CreateClient(new S7ClientOptions(), loggerFactory: log))
        {
            for (int i = 0; i < cycles; i++)
            {
                Succeeded(await client.ConnectAsync());
                Assert.Equal((ushort)0x0C0D, Succeeded(await client.ReadUInt16Async("DB1.DBW0")));
                await client.DisconnectAsync();
                Assert.Equal(ConnectionState.Disconnected, client.State);
            }
        }

        Assert.Equal(cycles, SentJobs(log).Count(job => FunctionOf(job) == SetupFunction));

        for (int i = 0; i < cycles; i++)
        {
            await using S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions()));
            Assert.Equal((ushort)0x0C0D, Succeeded(await client.ReadUInt16Async("DB1.DBW0")));
        }
    }

    /// <summary>心跳探测（读 MB0 的 1 字节，P4-D15）被服务器接受：空闲时持续发送，链路不离开 Connected。</summary>
    public static async Task HeartbeatStaysHealthyAsync(S7ToolWire wire)
    {
        const int idleMs = 2000;
        const int minimumProbes = 5;
        var settings = new S7ToolChannelSettings
        {
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 1000, MaxFailures = 2 },
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 100 },
        };

        var log = new TestLogger();
        await using S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), settings, log));
        int left = 0;
        client.StateChanged += (_, _) => Interlocked.Increment(ref left);

        await Task.Delay(idleMs);

        // 探测帧与 V-S7-10 同形，只是地址为 MB0：区域 83、位地址 000000、1 字节。
        byte[][] probes = SentJobs(log).Where(job => FunctionOf(job) == ReadFunction).ToArray();
        Assert.Equal(0, Volatile.Read(ref left));
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.True(probes.Length >= minimumProbes, $"Only {probes.Length} probes in {idleMs} ms.");
        Assert.All(probes, probe => Assert.Equal(ToBytes("00 01 00 00 83 00 00 00"), probe.Skip(23).Take(8).ToArray()));
        Assert.Equal(2, Succeeded(await client.ReadBytesAsync("MB0", 2)).Length);
    }

    /// <summary>
    /// 链路故障（经中继制造，服务器看到真实的断连）：断开、黑洞之后客户端重连、重新握手且读到的值正确；
    /// 迟到的应答（应答按发送顺序关联）不能被下一个请求认领，两种 <c>ResetOnRequestTimeout</c> 下都如此。
    /// </summary>
    public static async Task LinkFaultsAsync(S7ToolWire wire, ITestOutputHelper output)
    {
        const int requestTimeout = 500;
        const int recoveryMs = 15000;
        const int words = 8;
        ushort[] values = Enumerable.Range(0, words).Select(i => (ushort)(0x2000 + i)).ToArray();
        wire.Write(DataBlock, 1, 0, values.SelectMany(value => new[] { (byte)(value >> 8), (byte)value }).ToArray());

        await using var relay = new TcpFaultRelay(wire.Host, wire.Port);
        var settings = new S7ToolChannelSettings
        {
            RequestTimeout = requestTimeout,
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 200, Timeout = 300, MaxFailures = 2 },
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 100, MaxInterval = 500 },
        };
        var log = new TestLogger();
        await using (S7Client client = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), settings, log, relay.Port)))
        {
            Assert.Equal(values[1], Succeeded(await client.ReadUInt16Async("DB1.DBW2")));

            // 断开：心跳或读循环发现后重连，新连接重新握手。
            relay.DropConnections();
            await Polling.WaitUntilAsync(() => relay.Connections >= 2, recoveryMs);
            await ReadUntilRecoveredAsync(client, "DB1.DBW4", values[2], recoveryMs);
            output.WriteLine($"After the drop: {relay.Connections} connections, PDU {client.NegotiatedPduSize}.");

            // 黑洞：连接还在但没有应答。请求超时，链路重建，恢复后读到正确的值。
            relay.Blackhole = true;
            Assert.False((await client.ReadUInt16Async("DB1.DBW6")).IsSuccess);
            await Polling.WaitUntilAsync(() => client.State != ConnectionState.Connected, recoveryMs);
            relay.Blackhole = false;
            await ReadUntilRecoveredAsync(client, "DB1.DBW8", values[4], recoveryMs);
            output.WriteLine($"After the blackhole: {relay.Connections} connections, PDU {client.NegotiatedPduSize}.");
        }

        Assert.True(SentJobs(log).Count(job => FunctionOf(job) == SetupFunction) >= 3, "Every reconnect must repeat Setup Communication.");

        foreach (bool reset in new[] { true, false })
        {
            // 不开心跳；重建连接需要开启重连，否则超时后停在 Disconnected。
            var plainSettings = new S7ToolChannelSettings
            {
                RequestTimeout = requestTimeout,
                ResetOnRequestTimeout = reset,
                Reconnect = new ReconnectOptions { Enabled = true, Interval = 100, MaxInterval = 500 },
            };
            await using S7Client plain = await ConnectAsync(wire.CreateClient(new S7ClientOptions(), plainSettings, port: relay.Port));
            int connections = relay.Connections;
            Assert.Equal(values[0], Succeeded(await plain.ReadUInt16Async("DB1.DBW0")));

            // 旧应答在下一个请求等待期间到达：重建连接时它落在旧连接上；不重建时迟到窗口把它丢弃。
            relay.ReplyDelay = requestTimeout * 3;
            Assert.Equal(CommErrorKind.Timeout, (await plain.ReadUInt16Async("DB1.DBW10")).ErrorKind);
            relay.ReplyDelay = 0;

            await ReadUntilRecoveredAsync(plain, "DB1.DBW12", values[6], recoveryMs);
            for (int i = 0; i < 50; i++)
            {
                int word = i % words;
                Assert.Equal(values[word], Succeeded(await plain.ReadUInt16Async($"DB1.DBW{word * 2}")));
            }

            output.WriteLine($"Late reply with ResetOnRequestTimeout={reset}: {relay.Connections - connections} new connections.");
            Assert.Equal(reset ? connections + 1 : connections, relay.Connections);
        }
    }

    // ————— 辅助 —————

    /// <summary>连接客户端；失败时释放客户端并让测试失败。</summary>
    public static async Task<S7Client> ConnectAsync(S7Client client)
    {
        CommResult connected = await client.ConnectAsync();
        if (!connected.IsSuccess)
        {
            await client.DisposeAsync();
            Assert.Fail($"Connect failed: {connected.ErrorKind} {connected.ErrorMessage}");
        }

        return client;
    }

    private static int Packets(int length, int limit) => (length + limit - 1) / limit;

    private static int CountSince(TestLogger log, int mark, int function)
        => SentJobs(log).Skip(mark).Count(job => FunctionOf(job) == function);

    private static string Describe(Func<int> exchange)
    {
        try
        {
            return $"return code 0x{exchange():X2}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static async Task ReadUntilRecoveredAsync(S7Client client, string address, ushort expected, int milliseconds)
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
}
