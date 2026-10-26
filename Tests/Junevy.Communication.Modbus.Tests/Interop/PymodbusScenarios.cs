using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 与第三方从站 pymodbus 互通的场景（P7-D17 第 ①③④⑥ 项），网络与串口两个测试类共用。从站的协议实现与本仓库无关，
/// 因此能发现"客户端与本仓库的模拟从站犯了同一个错误"的问题。每个场景接收一个"按选项连接客户端"的委托与从站，数据布局见 <c>Interop/pymodbus_slave.py</c>。
/// </summary>
internal static class PymodbusScenarios
{
    /// <summary>请求超时（毫秒）。</summary>
    public const int RequestTimeout = 2000;

    private const int ReadHolding = 3;
    private const int WriteSingleCoil = 5;
    private const int WriteSingleRegister = 6;
    private const int WriteMultipleCoils = 15;
    private const int WriteMultipleRegisters = 16;
    private const int MaskWrite = 22;

    /// <summary>① 读写、类型、分包、0x16 / 0x17、站号前缀与异常应答。</summary>
    public static async Task RoundTripAsync(Func<ModbusClientOptions, Task<ModbusClient>> connect, PymodbusSlave slave)
    {
        using ModbusClient client = await connect(new ModbusClientOptions { UseMaskWriteForBits = true });

        // 读：数据由从站生成，客户端的解码与从站无关。
        Assert.Equal(new ushort[] { 1, 4, 7, 10, 13 }, Ok(await client.ReadUInt16Async("IR0", 5)));
        Assert.Equal((ushort)16, Ok(await client.ReadUInt16Async("30006")));
        Assert.Equal((1 << 16) | 4, Ok(await client.ReadInt32Async("IR0")));
        Assert.Equal(new[] { true, false, false, true, false, false, true, false }, Ok(await client.ReadBitsAsync("DI0", 8)));

        // 写单个与多个寄存器、线圈，并核对实际发出的功能码。
        int mark = slave.Requests.Count;
        Ok(await client.WriteUInt16Async("HR100", 0xBEEF));
        Ok(await client.WriteUInt16Async("HR110", new ushort[] { 1, 2, 3 }));
        Ok(await client.WriteBoolAsync("C5", true));
        Ok(await client.WriteBitsAsync("C20", new[] { true, false, true, true, false, false, false, true, true }));
        Assert.Equal(new[] { WriteSingleRegister, WriteMultipleRegisters, WriteSingleCoil, WriteMultipleCoils }, await FunctionsSinceAsync(slave, mark, 4));

        Assert.Equal((ushort)0xBEEF, Ok(await client.ReadUInt16Async("HR100")));
        Assert.Equal(new ushort[] { 1, 2, 3 }, Ok(await client.ReadUInt16Async("HR110", 3)));
        Assert.Equal(new[] { false, false, false, false, false, true, false, false }, Ok(await client.ReadBitsAsync("C0", 8)));
        Assert.Equal(new[] { true, false, true, true, false, false, false, true, true }, Ok(await client.ReadBitsAsync("C20", 9)));

        // 类型化读写（默认 ABCD）。
        Ok(await client.WriteInt32Async("HR200", 0x12345678));
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, Ok(await client.ReadUInt16Async("HR200", 2)));
        Ok(await client.WriteFloatAsync("HR202", 3.25f));
        Assert.Equal(3.25f, Ok(await client.ReadFloatAsync("HR202")));
        Ok(await client.WriteStringAsync("HR210", "Junevy"));
        Assert.Equal("Junevy", Ok(await client.ReadStringAsync("HR210", 6)));

        // 分包：读 200 个输入寄存器（125 + 75），写 300 个保持寄存器（123 + 123 + 54），读 2000 个离散输入（一包）。
        mark = slave.Requests.Count;
        ushort[] inputs = Ok(await client.ReadUInt16Async("IR0", 200));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (ushort)(i * 3 + 1)), inputs);
        Assert.Equal(2, (await FunctionsSinceAsync(slave, mark, 2)).Length);

        ushort[] block = Enumerable.Range(0, 300).Select(i => (ushort)(0x8000 + i)).ToArray();
        mark = slave.Requests.Count;
        Ok(await client.WriteUInt16Async("HR300", block));
        Assert.Equal(new[] { WriteMultipleRegisters, WriteMultipleRegisters, WriteMultipleRegisters }, await FunctionsSinceAsync(slave, mark, 3));
        Assert.Equal(block, Ok(await client.ReadUInt16Async("HR300", 300)));
        Assert.Equal(Enumerable.Range(0, 2000).Select(i => i % 3 == 0), Ok(await client.ReadBitsAsync("DI0", 2000)));

        // 0x16 掩码写：结果 = (当前 AND and) OR (or AND NOT and)。
        Ok(await client.WriteUInt16Async("HR140", 0x00F0));
        Ok(await client.MaskWriteRegisterAsync("HR140", andMask: 0x00FF, orMask: 0x1234));
        Assert.Equal((ushort)0x12F0, Ok(await client.ReadUInt16Async("HR140")));

        // 寄存器的位（UseMaskWriteForBits）走 0x16。
        mark = slave.Requests.Count;
        Ok(await client.WriteBoolAsync("HR150.3", true));
        Assert.Equal(new[] { MaskWrite }, await FunctionsSinceAsync(slave, mark, 1));
        Assert.Equal((ushort)0x0008, Ok(await client.ReadUInt16Async("HR150")));
        Assert.True(Ok(await client.ReadBoolAsync("HR150.3")));

        // 0x17：先写后读（同一地址读到刚写入的值）；不同地址时读写互不影响。
        byte[] sameAddress = Ok(await client.ReadWriteMultipleRegistersAsync("HR130", 2, "HR130", new byte[] { 0x00, 0x09, 0x00, 0x0A }));
        Assert.Equal(new byte[] { 0x00, 0x09, 0x00, 0x0A }, sameAddress);
        byte[] otherAddress = Ok(await client.ReadWriteMultipleRegistersAsync("HR110", 3, "HR120", new byte[] { 0x00, 0x07, 0x00, 0x08 }));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x02, 0x00, 0x03 }, otherAddress);
        Assert.Equal(new ushort[] { 7, 8 }, Ok(await client.ReadUInt16Async("HR120", 2)));

        // 站号前缀：站号 2 的数据与站号 1 分开。
        mark = slave.Requests.Count;
        Assert.Equal((ushort)0x0202, Ok(await client.ReadUInt16Async("s=2;HR0")));
        Assert.Equal((2, ReadHolding), (await slave.WaitForRequestsAsync(mark + 1))[mark]);
        Assert.Equal((ushort)0, Ok(await client.ReadUInt16Async("HR0")));

        // 异常应答：越界地址返回 02。之后链路照常可用。
        AssertRemoteError(await client.ReadUInt16Async("HR999", 2), 0x02, "Illegal Data Address");
        // 不存在的站号：规范上网关应回 0B；pymodbus 3.16 的 SimCore 查不到站号时内部 KeyError，回 04。两者都应解析为带站号 9 的异常应答。
        CommResult<ushort> unknownStation = await client.ReadUInt16Async("s=9;HR0");
        Assert.Equal(CommErrorKind.RemoteError, unknownStation.ErrorKind);
        Assert.Contains(unknownStation.ProtocolErrorCode, new long?[] { 0x04, 0x0B });
        Assert.Equal((ushort)0xBEEF, Ok(await client.ReadUInt16Async("HR100")));
    }

    /// <summary>③ 不支持 0x16 的设备：返回异常码 01；改用读改写可以绕过。</summary>
    public static async Task DeviceWithoutMaskWriteAsync(Func<ModbusClientOptions, Task<ModbusClient>> connect, PymodbusSlave slave)
    {
        using (ModbusClient client = await connect(new ModbusClientOptions { UseMaskWriteForBits = true }))
        {
            AssertRemoteError(await client.MaskWriteRegisterAsync("HR0", 0xFFFF, 0x0000), 0x01, "Illegal Function");
            AssertRemoteError(await client.WriteBoolAsync("HR0.1", true), 0x01, "Illegal Function");
            Assert.Equal((ushort)0, Ok(await client.ReadUInt16Async("HR0")));
        }

        // 绕过：关掉掩码写、允许读改写，位写入改为 03 + 06。
        using (ModbusClient client = await connect(new ModbusClientOptions { AllowReadModifyWriteBits = true }))
        {
            int mark = slave.Requests.Count;
            Ok(await client.WriteBoolAsync("HR0.1", true));
            Assert.Equal(new[] { ReadHolding, WriteSingleRegister }, await FunctionsSinceAsync(slave, mark, 2));
            Assert.Equal((ushort)0x0002, Ok(await client.ReadUInt16Async("HR0")));
        }
    }

    /// <summary>④ 只接受 0F / 10 的设备：默认选项返回异常码 01；UseSingleWriteFunctions = false 后只发 0F / 10。</summary>
    public static async Task DeviceWithoutSingleWritesAsync(Func<ModbusClientOptions, Task<ModbusClient>> connect, PymodbusSlave slave)
    {
        using (ModbusClient client = await connect(new ModbusClientOptions()))
        {
            AssertRemoteError(await client.WriteUInt16Async("HR0", 1), 0x01, "Illegal Function");
            AssertRemoteError(await client.WriteBoolAsync("C0", true), 0x01, "Illegal Function");
        }

        using (ModbusClient client = await connect(new ModbusClientOptions { UseSingleWriteFunctions = false }))
        {
            int mark = slave.Requests.Count;
            Ok(await client.WriteUInt16Async("HR0", 0x1111));
            Ok(await client.WriteBoolAsync("C0", true));
            // 124 个字：默认会拆成 10（123 个）+ 06（1 个）；关闭后两包都是 10。
            Ok(await client.WriteUInt16Async("HR10", Enumerable.Range(0, 124).Select(i => (ushort)i).ToArray()));
            Assert.Equal(new[] { WriteMultipleRegisters, WriteMultipleCoils, WriteMultipleRegisters, WriteMultipleRegisters },
                await FunctionsSinceAsync(slave, mark, 4));

            Assert.Equal((ushort)0x1111, Ok(await client.ReadUInt16Async("HR0")));
            Assert.True(Ok(await client.ReadBoolAsync("C0")));
            Assert.Equal((ushort)123, Ok(await client.ReadUInt16Async("HR133")));
        }
    }

    /// <summary>连接客户端；失败时释放客户端并让测试失败。</summary>
    public static async Task<ModbusClient> ConnectAsync(ModbusClient client)
    {
        CommResult connected = await client.ConnectAsync();
        if (!connected.IsSuccess)
        {
            client.Dispose();
            Assert.Fail($"Connect failed: {connected.ErrorKind} {connected.ErrorMessage}");
        }

        return client;
    }

    private static async Task<int[]> FunctionsSinceAsync(PymodbusSlave slave, int mark, int count)
    {
        IReadOnlyList<(int Unit, int Function)> requests = await slave.WaitForRequestsAsync(mark + count);
        return requests.Skip(mark).Select(request => request.Function).ToArray();
    }

    private static T Ok<T>(CommResult<T> result)
    {
        Assert.True(result.IsSuccess, $"{result.ErrorKind}: {result.ErrorMessage}");
        return result.Data!;
    }

    private static void Ok(CommResult result) => Assert.True(result.IsSuccess, $"{result.ErrorKind}: {result.ErrorMessage}");

    private static void AssertRemoteError(CommResult result, long code, string name)
        => AssertRemoteError(result.ErrorKind, result.ProtocolErrorCode, result.ErrorMessage, code, name);

    private static void AssertRemoteError<T>(CommResult<T> result, long code, string name)
        => AssertRemoteError(result.ErrorKind, result.ProtocolErrorCode, result.ErrorMessage, code, name);

    private static void AssertRemoteError(CommErrorKind kind, long? actualCode, string? message, long code, string name)
    {
        Assert.Equal(CommErrorKind.RemoteError, kind);
        Assert.Equal(code, actualCode);
        Assert.Contains(name, message);
    }
}
