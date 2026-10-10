using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 通道代码级覆盖（<see cref="TcpChannelComponents"/>）的构造时行为：纯构造测试，不建立连接，因此不在 SocketTiming 集合中。
/// </summary>
public sealed class TcpChannelComponentsTests
{
    [Fact(Timeout = 30000)]
    public void HealthProbeFactory_NotCalledAtConstruction()
    {
        // 客户端的工厂在第一次成功打开、启动心跳之前才调用：构造通道本身不调用它。
        int calls = 0;
        var config = new TcpClientChannelConfig
        {
            Host = "127.0.0.1",
            Port = 5000,
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, MaxFailures = 1 },
        };
        var components = new TcpChannelComponents
        {
            HealthProbeFactory = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("The client must not call the probe factory before it opens.");
            },
        };

        var client = new TcpClientChannel(config, null, components);

        Assert.Equal(ConnectionState.Disconnected, client.State);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact(Timeout = 30000)]
    public void HealthProbe_AndHealthProbeFactory_ThrowsOnClient()
    {
        var probe = new DelegateProbe(_ => Task.FromResult(CommResult.Success()));
        var config = new TcpClientChannelConfig { Host = "127.0.0.1", Port = 5000 };
        var components = new TcpChannelComponents
        {
            HealthProbe = probe,
            HealthProbeFactory = _ => probe,
        };

        Assert.Throws<ArgumentException>(() => new TcpClientChannel(config, null, components));
    }
}
