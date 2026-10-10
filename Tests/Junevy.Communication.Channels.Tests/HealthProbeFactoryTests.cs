using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// <see cref="ChannelComponents.HealthProbeFactory"/> 在字节流客户端上的行为：以通道自身调用一次、结果跨重连复用、
/// 工厂失败使本次打开失败，以及与 <see cref="ChannelComponents.HealthProbe"/> 互斥。使用 <see cref="DuplexClientChannel"/>（内存双工流）。
/// </summary>
public sealed class HealthProbeFactoryTests
{
    [Fact(Timeout = 30000)]
    public async Task HealthProbeFactory_ReceivesChannelAndIsReusedAcrossReconnects()
    {
        IByteChannel? received = null;
        int factoryCalls = 0;
        var probe = new ScriptedProbe(_ => Task.FromResult(CommResult.Success()));
        var components = new ChannelComponents
        {
            HealthProbeFactory = channel =>
            {
                Interlocked.Increment(ref factoryCalls);
                received = channel;
                return probe;
            },
        };
        var channel = new DuplexClientChannel(Settings(reconnect: true, heartbeat: true), components);
        await using var owned = channel;

        CommResult first = await WithinAsync(channel.ConnectAsync(), 3000);

        Assert.True(first.IsSuccess, first.ToString());
        Assert.Same(channel, received);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
        await WaitUntilAsync(() => probe.Calls >= 1, 5000);

        // 对端关闭：通道自动重连。工厂不再被调用，同一个探测继续驱动新连接的心跳。
        int callsBeforeLoss = probe.Calls;
        channel.LatestPair!.B.Dispose();
        await WaitUntilAsync(() => channel.State == ConnectionState.Connected && channel.OpenCount == 2, 5000);
        await WaitUntilAsync(() => probe.Calls > callsBeforeLoss, 5000);

        Assert.Equal(1, Volatile.Read(ref factoryCalls));
        Assert.Equal(1, channel.Statistics.ReconnectCount);
    }

    [Fact(Timeout = 30000)]
    public void HealthProbeFactory_AndHealthProbe_Throws()
    {
        var probe = new ScriptedProbe(_ => Task.FromResult(CommResult.Success()));
        var components = new ChannelComponents
        {
            HealthProbe = probe,
            HealthProbeFactory = _ => probe,
        };

        // 两者互斥：无论心跳是否启用，同时设置都在构造时抛出 ArgumentException。
        Assert.Throws<ArgumentException>(() => new DuplexClientChannel(Settings(heartbeat: true), components));
        Assert.Throws<ArgumentException>(() => new DuplexClientChannel(Settings(heartbeat: false), components));
    }

    [Fact(Timeout = 30000)]
    public async Task HealthProbeFactory_Throws_FailsOpen()
    {
        int calls = 0;
        var components = new ChannelComponents
        {
            HealthProbeFactory = _ =>
            {
                // 第一次打开时工厂抛出异常，第二次打开时返回 null：两者都使本次打开失败，且不会留下可用的探测。
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("The probe factory failed.");

                return null!;
            },
        };
        var channel = new DuplexClientChannel(Settings(heartbeat: true), components);
        await using var owned = channel;

        CommResult thrown = await WithinAsync(channel.ConnectAsync(), 3000);
        Assert.False(thrown.IsSuccess);
        Assert.Equal(CommErrorKind.Unspecified, thrown.ErrorKind);
        Assert.Contains("HealthProbeFactory", thrown.ErrorMessage);
        Assert.Equal(ConnectionState.Disconnected, channel.State);

        CommResult returnedNull = await WithinAsync(channel.ConnectAsync(), 3000);
        Assert.False(returnedNull.IsSuccess);
        Assert.Equal(CommErrorKind.Unspecified, returnedNull.ErrorKind);
        Assert.Contains("HealthProbeFactory", returnedNull.ErrorMessage);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    // 工厂只在启用心跳时调用：未启用时不得调用，也不得因此失败。
    [Fact(Timeout = 30000)]
    public async Task HealthProbeFactory_NotCalledWhenHeartbeatIsDisabled()
    {
        int calls = 0;
        var components = new ChannelComponents
        {
            HealthProbeFactory = _ =>
            {
                Interlocked.Increment(ref calls);
                return new ScriptedProbe(_ => Task.FromResult(CommResult.Success()));
            },
        };
        var channel = new DuplexClientChannel(Settings(heartbeat: false), components);
        await using var owned = channel;

        Assert.True((await WithinAsync(channel.ConnectAsync(), 3000)).IsSuccess);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    private static ClientChannelSettings Settings(bool reconnect = false, bool heartbeat = false)
    {
        var settings = new ClientChannelSettings
        {
            SendTimeout = 2000,
            RequestTimeout = 2000,
            DisconnectTimeout = 0,
            Framing = new FramingOptions { Mode = FramingMode.Raw },
        };

        if (reconnect)
            settings.Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 50, MaxAttempts = 0 };

        if (heartbeat)
            settings.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 50, Timeout = 200, MaxFailures = 3 };

        return settings;
    }
}
