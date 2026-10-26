using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 与第三方从站 pymodbus 的互通验证，网络传输（P7-D17 第 ①③④⑥ 项）。需要 <c>JUNEVY_PYMODBUS_PYTHON</c>。
/// 场景见 <see cref="PymodbusScenarios"/>；串口见 <see cref="PymodbusSerialInteropTests"/>。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class PymodbusInteropTests
{
    private enum Transport
    {
        TcpMbap,
        TcpRtu,
        UdpMbap,
        UdpRtu,
    }

    // ⑥ RTU over UDP 是其中的 UdpRtu。

    [PymodbusFact(Timeout = 120000)]
    public Task RoundTrip_TcpMbap() => RunAsync(Transport.TcpMbap, string.Empty, PymodbusScenarios.RoundTripAsync);

    [PymodbusFact(Timeout = 120000)]
    public Task RoundTrip_TcpRtu() => RunAsync(Transport.TcpRtu, string.Empty, PymodbusScenarios.RoundTripAsync);

    [PymodbusFact(Timeout = 120000)]
    public Task RoundTrip_UdpMbap() => RunAsync(Transport.UdpMbap, string.Empty, PymodbusScenarios.RoundTripAsync);

    [PymodbusFact(Timeout = 120000)]
    public Task RoundTrip_UdpRtu() => RunAsync(Transport.UdpRtu, string.Empty, PymodbusScenarios.RoundTripAsync);

    [PymodbusFact(Timeout = 120000)]
    public Task DeviceWithoutMaskWrite_TcpMbap() => RunAsync(Transport.TcpMbap, "22", PymodbusScenarios.DeviceWithoutMaskWriteAsync);

    [PymodbusFact(Timeout = 120000)]
    public Task DeviceWithoutMaskWrite_TcpRtu() => RunAsync(Transport.TcpRtu, "22", PymodbusScenarios.DeviceWithoutMaskWriteAsync);

    [PymodbusFact(Timeout = 120000)]
    public Task DeviceWithoutSingleWrites_TcpMbap() => RunAsync(Transport.TcpMbap, "5,6", PymodbusScenarios.DeviceWithoutSingleWritesAsync);

    [PymodbusFact(Timeout = 120000)]
    public Task DeviceWithoutSingleWrites_UdpRtu() => RunAsync(Transport.UdpRtu, "5,6", PymodbusScenarios.DeviceWithoutSingleWritesAsync);

    private static async Task RunAsync(Transport transport, string reject, Func<Func<ModbusClientOptions, Task<ModbusClient>>, PymodbusSlave, Task> scenario)
    {
        bool tcp = transport is Transport.TcpMbap or Transport.TcpRtu;
        ModbusFraming framing = transport is Transport.TcpMbap or Transport.UdpMbap ? ModbusFraming.Mbap : ModbusFraming.Rtu;
        await using PymodbusSlave slave = tcp
            ? await PymodbusSlave.StartTcpAsync(framing, reject)
            : await PymodbusSlave.StartUdpAsync(framing, reject);

        await scenario(options =>
        {
            options.Framing = framing;
            return PymodbusScenarios.ConnectAsync(tcp
                ? ModbusClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = slave.Port, RequestTimeout = PymodbusScenarios.RequestTimeout }, options)
                : ModbusClient.Create(new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = slave.Port, RequestTimeout = PymodbusScenarios.RequestTimeout }, options));
        }, slave);
    }
}
