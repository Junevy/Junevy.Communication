using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 通道代码级覆盖在客户端上的行为：服务端专用的按会话探测工厂对客户端无效，设置它不会报错（纯构造测试，不建立连接）。
/// </summary>
public sealed class TcpChannelComponentsTests
{
    [Fact(Timeout = 30000)]
    public void SessionHealthProbeFactory_IgnoredByClient()
    {
        var config = new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = 5000,
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, MaxFailures = 1, Payload = "PING" },
        };
        var components = new TcpChannelComponents
        {
            SessionHealthProbeFactory = _ => throw new InvalidOperationException("The client must not call the session probe factory."),
        };

        var client = new TcpClientChannel(config, null, components);

        Assert.Equal(ConnectionState.Disconnected, client.State);
    }
}
