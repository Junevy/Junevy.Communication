using System.IO.Ports;
using Junevy.Communication.Serial;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 真实串口端到端（计划 Task 12，可选）：需设置 JUNEVY_SERIAL_PAIR=COMx,COMy（例如 COM20,COM21），未设置时跳过。
/// 客户端经 McClient.CreateSerial 连接第一个串口，设备替身在第二个串口的 BaseStream 上运行。
/// 执行者不运行本测试，由审阅者在具有串口对的机器上运行。
/// </summary>
public sealed class McSerialPairTests
{
    [SerialPairFact(Timeout = 180000)]
    public async Task Format5WithChecksum_RoundTripsOverARealSerialPair()
    {
        (string first, string second) = SerialPairEnvironment.Read();
        var options = new McClientOptions { FrameType = McFrameType.Frame4C, Encoding = McEncoding.Binary, SumCheck = true };
        var adapter = new McDeviceAdapter(McFrameType.Frame4C, McEncoding.Binary, sumCheck: true, new PlcMemory());
        using var devicePort = new SerialPort(second, 9600, Parity.None, 8, StopBits.One);
        devicePort.Open();
        using var stopDevice = new CancellationTokenSource();
        var simulator = new DeviceSimulator(McEndToEndSupport.DeviceFraming(options), adapter.Handle);
        Task device = simulator.RunAsync(devicePort.BaseStream, stopDevice.Token);

        using McClient client = McClient.CreateSerial(new SerialChannelConfig { PortName = first, BaudRate = 9600 }, options);
        try
        {
            Assert.True((await client.ConnectAsync()).IsSuccess);
            await McEndToEndSupport.AssertTypedRoundTripAsync(client);
        }
        finally
        {
            stopDevice.Cancel();
            await device.ContinueWith(_ => { }, TaskScheduler.Default);   // 等待设备替身退出后再关闭端口
        }
    }
}
