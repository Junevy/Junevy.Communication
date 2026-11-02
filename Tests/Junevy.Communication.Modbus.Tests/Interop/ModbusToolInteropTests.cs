using Xunit.Abstractions;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 与 Modbus Slave 软件核对，Modbus TCP。需要 <c>JUNEVY_MODBUS_TOOL_TCP</c>（如 <c>127.0.0.1:502</c>）。场景见 <see cref="ModbusToolScenarios"/>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class ModbusToolTcpInteropTests
{
    private const string Variable = ModbusToolFactAttribute.TcpVariable;

    private readonly ITestOutputHelper output;

    public ModbusToolTcpInteropTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task ReadsDecodeWhatTheWireHolds() => RunAsync(ModbusToolScenarios.ReadsDecodeWhatTheWireHoldsAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task WritesLandOnTheWire() => RunAsync(ModbusToolScenarios.WritesLandOnTheWireAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task MaskWriteAndReadWrite() => RunAsync(ModbusToolScenarios.MaskWriteAndReadWriteAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task ExceptionResponses() => RunAsync(ModbusToolScenarios.ExceptionResponsesAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task Coils() => RunAsync(wire => ModbusToolScenarios.CoilsAsync(wire, output));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task DiscreteInputs() => RunAsync(wire => ModbusToolScenarios.DiscreteInputsAsync(wire, output));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task InputRegisters() => RunAsync(wire => ModbusToolScenarios.InputRegistersAsync(wire, output));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task Stations() => RunAsync(wire => ModbusToolScenarios.TcpStationsAsync(wire, output));

    // Modbus Slave 每个请求约 31 ms（手工帧同样），次数按这个速度定：超时被放弃的用例还在后台写寄存器，会干扰之后的用例。
    [ModbusToolFact(Variable, Timeout = 180000)]
    public Task SoakAndConcurrency() => RunAsync(wire => ModbusToolScenarios.SoakAndConcurrencyAsync(wire, sequentialReads: 500, callers: 16, readsPerCaller: 25, writesPerRegister: 20, output));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task SeveralClients() => RunAsync(wire => ModbusToolScenarios.SeveralClientsAsync(wire, clients: 4, readsPerClient: 200));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task ConnectCycles() => RunAsync(wire => ModbusToolScenarios.ConnectCyclesAsync(wire, cycles: 20));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task HeartbeatStaysHealthy() => RunAsync(ModbusToolScenarios.HeartbeatStaysHealthyAsync);

    [ModbusToolFact(Variable, Timeout = 120000)]
    public Task LinkFaults()
    {
        ModbusToolTcpWire wire = ModbusToolWire.Tcp();
        return wire.KeepDataAsync(() => ModbusToolScenarios.TcpLinkFaultsAsync(wire, output));
    }

    private static Task RunAsync(Func<ModbusToolWire, Task> scenario)
    {
        ModbusToolWire wire = ModbusToolWire.Tcp();
        return wire.KeepDataAsync(() => scenario(wire));
    }
}

/// <summary>
/// 与 Modbus Slave 软件核对，串口 RTU。需要 <c>JUNEVY_MODBUS_TOOL_SERIAL</c>（客户端使用的端口，Modbus Slave 在虚拟对的另一端）。
/// 与其他打开真实串口的测试同在 <see cref="RealSerialPairCollection"/>。场景见 <see cref="ModbusToolScenarios"/>。
/// </summary>
[Collection(RealSerialPairCollection.Name)]
public sealed class ModbusToolSerialInteropTests
{
    private const string Variable = ModbusToolFactAttribute.SerialVariable;

    private readonly ITestOutputHelper output;

    public ModbusToolSerialInteropTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task ReadsDecodeWhatTheWireHolds() => RunAsync(ModbusToolScenarios.ReadsDecodeWhatTheWireHoldsAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task WritesLandOnTheWire() => RunAsync(ModbusToolScenarios.WritesLandOnTheWireAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task MaskWriteAndReadWrite() => RunAsync(ModbusToolScenarios.MaskWriteAndReadWriteAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task ExceptionResponses() => RunAsync(ModbusToolScenarios.ExceptionResponsesAsync);

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task Coils() => RunAsync(wire => ModbusToolScenarios.CoilsAsync(wire, output));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task DiscreteInputs() => RunAsync(wire => ModbusToolScenarios.DiscreteInputsAsync(wire, output));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task InputRegisters() => RunAsync(wire => ModbusToolScenarios.InputRegistersAsync(wire, output));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task Stations() => RunAsync(ModbusToolScenarios.SerialStationsAsync);

    [ModbusToolFact(Variable, Timeout = 180000)]
    public Task SoakAndConcurrency() => RunAsync(wire => ModbusToolScenarios.SoakAndConcurrencyAsync(wire, sequentialReads: 300, callers: 8, readsPerCaller: 25, writesPerRegister: 5, output));

    [ModbusToolFact(Variable, Timeout = 120000)]
    public Task ConnectCycles() => RunAsync(wire => ModbusToolScenarios.ConnectCyclesAsync(wire, cycles: 10));

    [ModbusToolFact(Variable, Timeout = 60000)]
    public Task HeartbeatStaysHealthy() => RunAsync(ModbusToolScenarios.HeartbeatStaysHealthyAsync);

    private static Task RunAsync(Func<ModbusToolWire, Task> scenario)
    {
        ModbusToolWire wire = ModbusToolWire.Serial();
        return wire.KeepDataAsync(() => scenario(wire));
    }
}
