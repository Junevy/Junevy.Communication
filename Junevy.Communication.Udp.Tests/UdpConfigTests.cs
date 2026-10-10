using System.Net;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Udp.Tests;

/// <summary>配置校验（D5）与配置快照的测试：非法值抛出 ArgumentException 族，且不改写调用方的配置对象。</summary>
public sealed class UdpConfigTests
{
    [Fact(Timeout = 30000)]
    public void Config_Invalid_Throws()
    {
        var notThrown = new List<string>();
        foreach ((string name, Action<UdpChannelConfig> mutate, ChannelComponents? components) in InvalidCases())
        {
            var config = new UdpChannelConfig();
            mutate(config);
            try
            {
                new UdpChannel(config, null, components).Dispose();
                notThrown.Add(name);
            }
            catch (ArgumentException)
            {
                // 期望的结果。
            }
        }

        Assert.Empty(notThrown);
    }

    [Fact(Timeout = 30000)]
    public void Config_DoesNotModifyCallerObject()
    {
        var config = new UdpChannelConfig
        {
            RemoteHost = "127.0.0.1",
            RemotePort = 9,
            MulticastGroups = null,
            Heartbeat = new HeartbeatOptions { Enabled = false, Interval = 0 },
        };

        new UdpChannel(config).Dispose();

        Assert.Equal("0.0.0.0", config.LocalAddress);
        Assert.Equal(0, config.LocalPort);
        Assert.Equal("127.0.0.1", config.RemoteHost);
        Assert.Equal(9, config.RemotePort);
        Assert.Null(config.MulticastGroups);
        Assert.Equal(0, config.Heartbeat.Interval);
        Assert.Equal(2000, config.RequestTimeout);
        Assert.Equal(65507, config.MaxDatagramSize);
    }

    [Fact(Timeout = 30000)]
    public async Task Config_LaterChanges_AreNotSeenByChannel()
    {
        var config = new UdpChannelConfig
        {
            LocalAddress = "127.0.0.1",
            RemoteHost = "127.0.0.1",
            RemotePort = 9,
        };
        await using var channel = new UdpChannel(config);

        // 构造之后修改配置不影响已创建的通道（快照语义）。
        config.RemotePort = 10;
        config.MaxDatagramSize = 1;

        Assert.Equal("udp://127.0.0.1:0->127.0.0.1:9", channel.Name);
        Assert.Same(config, channel.Config);
    }

    [Fact(Timeout = 30000)]
    public void Name_Default_DescribesLocalAndRemoteEndPoints()
    {
        using var undirected = new UdpChannel(new UdpChannelConfig { LocalAddress = "127.0.0.1", LocalPort = 0 });
        using var directed = new UdpChannel(new UdpChannelConfig { LocalAddress = "127.0.0.1", RemoteHost = "127.0.0.1", RemotePort = 9 });
        using var named = new UdpChannel("named", new UdpChannelConfig { LocalAddress = "127.0.0.1" });

        Assert.Equal("udp://127.0.0.1:0", undirected.Name);
        Assert.Equal("udp://127.0.0.1:0->127.0.0.1:9", directed.Name);
        Assert.Equal("named", named.Name);
    }

    [Fact(Timeout = 30000)]
    public void Config_ValidValues_Construct()
    {
        using var channel = new UdpChannel(new UdpChannelConfig
        {
            LocalAddress = "0.0.0.0",
            MulticastGroups = new[] { "239.255.0.1", "ff02::1" },
            MulticastTimeToLive = 255,
            MaxDatagramSize = 65507,
            RequestRetryCount = 3,
            LateReplyWindow = 0,
            Heartbeat = new HeartbeatOptions { Enabled = true, Payload = "hex:0A", Interval = 1, Timeout = 1, MaxFailures = 1 },
            RemoteHost = "localhost",
            RemotePort = 65535,
        });

        Assert.Equal("udp://0.0.0.0:0->localhost:65535", channel.Name);
    }

    private static IEnumerable<(string Name, Action<UdpChannelConfig> Mutate, ChannelComponents? Components)> InvalidCases()
    {
        yield return ("LocalAddress is not an IP address", c => c.LocalAddress = "not-an-address", null);
        yield return ("LocalPort is negative", c => c.LocalPort = -1, null);
        yield return ("LocalPort is above 65535", c => c.LocalPort = 65536, null);
        yield return ("RemoteHost without RemotePort", c => c.RemoteHost = "127.0.0.1", null);
        yield return ("RemotePort without RemoteHost", c => c.RemotePort = 9, null);
        yield return ("RemotePort is out of range", c => { c.RemoteHost = "127.0.0.1"; c.RemotePort = 70000; }, null);
        yield return ("RemoteHost is not a valid host name", c => { c.RemoteHost = "bad host name!"; c.RemotePort = 9; }, null);
        yield return ("MaxDatagramSize is zero", c => c.MaxDatagramSize = 0, null);
        yield return ("MaxDatagramSize is above the UDP limit", c => c.MaxDatagramSize = 65508, null);
        yield return ("ReceiveBufferSize is zero", c => c.ReceiveBufferSize = 0, null);
        yield return ("SendTimeout is zero", c => c.SendTimeout = 0, null);
        yield return ("RequestTimeout is zero", c => c.RequestTimeout = 0, null);
        yield return ("HandshakeTimeout is zero", c => c.HandshakeTimeout = 0, null);
        yield return ("DisconnectTimeout is negative", c => c.DisconnectTimeout = -1, null);
        yield return ("RequestRetryCount is negative", c => c.RequestRetryCount = -1, null);
        yield return ("IdleTimeout is negative", c => c.IdleTimeout = -1, null);
        yield return ("LateReplyWindow is below -1", c => c.LateReplyWindow = -2, null);
        yield return ("MulticastTimeToLive is above 255", c => c.MulticastTimeToLive = 256, null);
        yield return ("MulticastTimeToLive is negative", c => c.MulticastTimeToLive = -1, null);
        yield return ("MulticastGroups contains a unicast address", c => c.MulticastGroups = new[] { "10.0.0.1" }, null);
        yield return ("MulticastGroups contains a non-address", c => c.MulticastGroups = new[] { "not-an-address" }, null);
        yield return ("ReceiveQueueCapacity is zero", c => c.ReceiveQueueCapacity = 0, null);
        yield return ("QueueFullMode is undefined", c => c.QueueFullMode = (QueueFullMode)99, null);
        yield return ("Correlation is undefined", c => c.Correlation = (CorrelationMode)99, null);
        yield return ("Heartbeat without a probe on an undirected channel",
            c => c.Heartbeat = new HeartbeatOptions { Enabled = true, Payload = "PING", Interval = 100, Timeout = 100, MaxFailures = 1 }, null);
        yield return ("Heartbeat interval is zero",
            c => { c.RemoteHost = "127.0.0.1"; c.RemotePort = 9; c.Heartbeat = new HeartbeatOptions { Enabled = true, Payload = "PING", Interval = 0 }; }, null);
        yield return ("Heartbeat without a payload",
            c => { c.RemoteHost = "127.0.0.1"; c.RemotePort = 9; c.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100 }; }, null);
        yield return ("Heartbeat payload is not a valid byte sequence",
            c => { c.RemoteHost = "127.0.0.1"; c.RemotePort = 9; c.Heartbeat = new HeartbeatOptions { Enabled = true, Payload = "hex:ZZ", Interval = 100, Timeout = 100 }; }, null);
        yield return ("FrameCodec is supplied", _ => { }, new ChannelComponents { FrameCodec = new NoFramingCodec() });
        yield return ("Keyed correlation without a key extractor", _ => { }, new ChannelComponents { Correlation = CorrelationMode.Keyed });
    }

    // 仅用于校验测试：UDP 不分帧，任何分帧工厂都应被拒绝，因此这里的成员永远不会被调用。
    private sealed class NoFramingCodec : IFrameCodecFactory
    {
        public IFrameDecoder CreateDecoder() => throw new NotSupportedException();

        public IFrameEncoder CreateEncoder() => throw new NotSupportedException();
    }
}
