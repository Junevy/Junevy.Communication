using Junevy.Communication.Testing;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 共享库 <see cref="LoopbackTcp"/> 的契约（计划 Task 2）：三个方法逐字段符合文档，每次调用返回新实例。
/// </summary>
/// <remarks>
/// 这些用例只构造配置对象、不建立连接，因此不属于 <c>SocketTiming</c> 集合，可以并行运行。
/// 放在 Tcp.Tests 而不是 Channels.Tests，是因为只有这里已经引用了 <c>Junevy.Communication.Tcp</c>。</remarks>
public sealed class LoopbackTcpTests
{
    [Fact]
    public void Client_SetsOnlyHostAndPort()
    {
        TcpClientChannelConfig config = LoopbackTcp.Client(9600);

        Assert.Equal(LoopbackTcp.Host, config.Host);
        Assert.Equal(9600, config.Port);
        Assert.False(config.Reconnect.Enabled);
        Assert.False(config.Heartbeat.Enabled);
    }

    [Fact]
    public void Reconnecting_EnablesReconnectWithTheGivenInterval()
    {
        TcpClientChannelConfig config = LoopbackTcp.Reconnecting(9600, reconnectIntervalMs: 75);

        Assert.Equal(LoopbackTcp.Host, config.Host);
        Assert.Equal(9600, config.Port);
        Assert.True(config.Reconnect.Enabled);
        Assert.Equal(75, config.Reconnect.Interval);
        Assert.False(config.Heartbeat.Enabled);
    }

    [Fact]
    public void Reconnecting_DefaultsToAHundredMilliseconds()
        => Assert.Equal(100, LoopbackTcp.Reconnecting(102).Reconnect.Interval);

    [Fact]
    public void Reconnecting_HonoursTheIntervalArgument()
        => Assert.Equal(30, LoopbackTcp.Reconnecting(102, reconnectIntervalMs: 30).Reconnect.Interval);

    [Fact]
    public void WithHeartbeat_SetsBothHeartbeatAndReconnect()
    {
        TcpClientChannelConfig config = LoopbackTcp.WithHeartbeat(102);

        Assert.Equal(LoopbackTcp.Host, config.Host);
        Assert.Equal(102, config.Port);
        Assert.True(config.Heartbeat.Enabled);
        Assert.Equal(100, config.Heartbeat.Interval);
        Assert.Equal(200, config.Heartbeat.Timeout);
        Assert.Equal(2, config.Heartbeat.MaxFailures);
        Assert.True(config.Reconnect.Enabled);
        Assert.Equal(200, config.Reconnect.Interval);
    }

    [Fact]
    public void WithHeartbeat_HonoursEveryArgument()
    {
        TcpClientChannelConfig config = LoopbackTcp.WithHeartbeat(102, intervalMs: 50, timeoutMs: 60, maxFailures: 4, reconnectIntervalMs: 70);

        Assert.Equal(50, config.Heartbeat.Interval);
        Assert.Equal(60, config.Heartbeat.Timeout);
        Assert.Equal(4, config.Heartbeat.MaxFailures);
        Assert.Equal(70, config.Reconnect.Interval);
    }

    [Fact]
    public void EveryCallReturnsAFreshInstance()
    {
        // 调用方经常在拿到配置后继续改（例如换成脚本服务器的随机端口），因此每次调用必须是新对象。
        Assert.NotSame(LoopbackTcp.Client(102), LoopbackTcp.Client(102));
        Assert.NotSame(LoopbackTcp.Reconnecting(102), LoopbackTcp.Reconnecting(102));
        Assert.NotSame(LoopbackTcp.WithHeartbeat(102), LoopbackTcp.WithHeartbeat(102));
    }
}