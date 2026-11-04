using Xunit.Abstractions;

namespace Junevy.Communication.Siemens.Tests.Interop;

/// <summary>
/// 与第三方 S7 服务器软件核对（ISO-on-TCP）。需要 <c>JUNEVY_S7_TOOL_TCP</c>（如 <c>127.0.0.1:102</c>）。场景见 <see cref="S7ToolScenarios"/>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class S7ToolInteropTests
{
    private readonly ITestOutputHelper output;

    public S7ToolInteropTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [S7ToolFact(Timeout = 60000)]
    public Task Handshake() => RunAsync(wire => S7ToolScenarios.HandshakeAsync(wire, output));

    [S7ToolFact(Timeout = 60000)]
    public Task ReadsDecodeWhatTheWireHolds() => RunAsync(S7ToolScenarios.ReadsDecodeWhatTheWireHoldsAsync);

    [S7ToolFact(Timeout = 60000)]
    public Task WritesLandOnTheWire() => RunAsync(S7ToolScenarios.WritesLandOnTheWireAsync);

    [S7ToolFact(Timeout = 60000)]
    public Task Bits() => RunAsync(S7ToolScenarios.BitsAsync);

    [S7ToolFact(Timeout = 60000)]
    public Task S7Strings() => RunAsync(S7ToolScenarios.S7StringsAsync);

    [S7ToolFact(Timeout = 60000)]
    public Task PacketSplitting() => RunAsync(wire => S7ToolScenarios.PacketSplittingAsync(wire, output));

    [S7ToolFact(Timeout = 60000)]
    public Task RemoteErrors() => RunAsync(S7ToolScenarios.RemoteErrorsAsync);

    [S7ToolFact(Timeout = 180000)]
    public Task SoakAndConcurrency() => RunAsync(wire => S7ToolScenarios.SoakAndConcurrencyAsync(wire, sequentialReads: 500, callers: 16, readsPerCaller: 25, writesPerWord: 20, output));

    [S7ToolFact(Timeout = 60000)]
    public Task SeveralClients() => RunAsync(wire => S7ToolScenarios.SeveralClientsAsync(wire, clients: 4, readsPerClient: 200));

    [S7ToolFact(Timeout = 60000)]
    public Task ConnectCycles() => RunAsync(wire => S7ToolScenarios.ConnectCyclesAsync(wire, cycles: 20));

    [S7ToolFact(Timeout = 60000)]
    public Task HeartbeatStaysHealthy() => RunAsync(S7ToolScenarios.HeartbeatStaysHealthyAsync);

    [S7ToolFact(Timeout = 120000)]
    public Task LinkFaults() => RunAsync(wire => S7ToolScenarios.LinkFaultsAsync(wire, output));

    private static Task RunAsync(Func<S7ToolWire, Task> scenario)
    {
        S7ToolWire wire = S7ToolWire.FromEnvironment();
        return wire.KeepDataAsync(() => scenario(wire));
    }
}
