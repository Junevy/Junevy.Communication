using System.IO.Ports;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Testing;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// 串口页测试（计划 Task 5 第 2 条）。只有列表、默认值与连接前的校验不需要硬件；
/// 真实的请求与应答只在 <c>JUNEVY_SERIAL_PAIR</c> 指定的一对互连端口上运行（例如 COM20,COM21），未设置时跳过。
/// </summary>
public sealed class SerialPageTests
{
    [Fact]
    public async Task Ports_AreListed_WithoutSelectingOneOrOpeningAny()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.SerialPage(dispatcher);

        Assert.Equal(SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase), page.AvailablePorts);
        Assert.Equal(string.Empty, page.PortName);
        Assert.Equal(FramingMode.IdleGap, page.Settings.Framing.Mode);
        Assert.Equal(SessionState.NotConnected, page.State);
    }

    [Fact]
    public async Task Heartbeat_WithoutExpectedReply_IsRefused_BeforeThePortIsOpened()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.SerialPage(dispatcher);
        // 端口名不存在：如果先打开端口，错误会是"找不到端口"，而不是心跳的校验。
        page.PortName = "COM250";
        page.Settings.Heartbeat.Enabled = true;
        page.Settings.Heartbeat.Payload = "hex:00";
        page.Settings.Heartbeat.ExpectedReply = string.Empty;

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.False(record.Succeeded);
        Assert.Equal(CommErrorKind.InvalidRequest, record.ErrorKind);
        Assert.Contains("ExpectedReply", record.Message);
    }

    [SerialPairFact]
    public async Task RequestAndReply_OverTheVirtualPair()
    {
        (string first, string second) = SerialPairEnvironment.Read();
        var dispatcher = new ManualUiDispatcher();
        using var peer = new SerialPort(second, 9600) { ReadTimeout = 5000, WriteTimeout = 5000 };
        peer.Open();
        // 对端读满三个字节，然后回复三个字节。
        Task<byte[]> peerReceived = Task.Run(() =>
        {
            var buffer = new byte[3];
            int read = 0;
            while (read < buffer.Length)
            {
                read += peer.Read(buffer, read, buffer.Length - read);
            }

            peer.Write([0x04, 0x05, 0x06], 0, 3);
            return buffer;
        });
        await using var page = ChannelTestKit.SerialPage(dispatcher);
        page.PortName = first;
        page.BaudRate = 9600;
        await page.ConnectCommand.ExecuteAsync(null);

        page.PayloadText = "hex:01 02 03";
        await page.RequestCommand.ExecuteAsync(null);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, await peerReceived.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new byte[] { 0x04, 0x05, 0x06 }, Assert.Single(page.ReceivedFrames).Data);
    }
}
