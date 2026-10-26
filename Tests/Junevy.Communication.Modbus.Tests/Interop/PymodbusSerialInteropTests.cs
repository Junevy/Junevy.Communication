using Junevy.Communication.Serial;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 与第三方从站 pymodbus 的互通验证，串口 RTU。需要 <c>JUNEVY_PYMODBUS_PYTHON</c> 与 <c>JUNEVY_SERIAL_PAIR</c>（客户端用第一个端口，从站用第二个）。
/// 与其他打开真实串口对的测试同在 <see cref="RealSerialPairCollection"/>，避免同时占用端口。场景见 <see cref="PymodbusScenarios"/>。
/// </summary>
[Collection(RealSerialPairCollection.Name)]
public sealed class PymodbusSerialInteropTests
{
    private const int BaudRate = 9600;

    [PymodbusFact(NeedsSerialPair = true, Timeout = 180000)]
    public Task RoundTrip_SerialRtu() => RunAsync(string.Empty, PymodbusScenarios.RoundTripAsync);

    [PymodbusFact(NeedsSerialPair = true, Timeout = 180000)]
    public Task DeviceWithoutMaskWrite_SerialRtu() => RunAsync("22", PymodbusScenarios.DeviceWithoutMaskWriteAsync);

    [PymodbusFact(NeedsSerialPair = true, Timeout = 180000)]
    public Task DeviceWithoutSingleWrites_SerialRtu() => RunAsync("5,6", PymodbusScenarios.DeviceWithoutSingleWritesAsync);

    private static async Task RunAsync(string reject, Func<Func<ModbusClientOptions, Task<ModbusClient>>, PymodbusSlave, Task> scenario)
    {
        (string clientPort, string slavePort) = SerialPairEnvironment.Read();
        await using PymodbusSlave slave = await PymodbusSlave.StartSerialAsync(slavePort, BaudRate, reject);

        await scenario(options => PymodbusScenarios.ConnectAsync(ModbusClient.Create(
            new SerialChannelConfig { PortName = clientPort, BaudRate = BaudRate, RequestTimeout = PymodbusScenarios.RequestTimeout }, options)), slave);
    }
}
