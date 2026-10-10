using System.Reflection;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 服务端配置的构造时校验与不改写（D5）。纯配置测试，不使用套接字，因此不在 SocketTiming 集合中，可与其他用例并行。
/// </summary>
public sealed class TcpServerConfigTests
{
    [Fact(Timeout = 30000)]
    public void ValidConfig_Constructs()
    {
        using var server = new TcpServer(new TcpServerConfig { Port = 5000 });

        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact(Timeout = 30000)]
    public void NullConfig_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new TcpServer(null!));
    }

    [Fact(Timeout = 30000)]
    public void Port_OutOfRange_Throws()
    {
        AssertRejected(config => config.Port = 0);
        AssertRejected(config => config.Port = -1);
        AssertRejected(config => config.Port = 65536);
    }

    [Fact(Timeout = 30000)]
    public void ListenAddress_NotAnIpAddress_Throws()
    {
        AssertRejected(config => config.ListenAddress = "localhost");
        AssertRejected(config => config.ListenAddress = string.Empty);
        AssertRejected(config => config.ListenAddress = null!);
    }

    [Fact(Timeout = 30000)]
    public void Backlog_NotPositive_Throws()
    {
        AssertRejected(config => config.Backlog = 0);
        AssertRejected(config => config.Backlog = -1);
    }

    [Fact(Timeout = 30000)]
    public void MaxSessions_Negative_Throws()
    {
        AssertRejected(config => config.MaxSessions = -1);
    }

    [Fact(Timeout = 30000)]
    public void RequiredTimeouts_MustBePositive()
    {
        AssertRejected(config => config.SendTimeout = 0);
        AssertRejected(config => config.RequestTimeout = 0);
        AssertRejected(config => config.StopTimeout = 0);
        AssertRejected(config => config.SessionHandshakeTimeout = 0);
        AssertRejected(config => config.SendTimeout = -1);
    }

    [Fact(Timeout = 30000)]
    public void OptionalTimeouts_MustNotBeNegative()
    {
        AssertRejected(config => config.SessionIdleTimeout = -1);
        AssertRejected(config => config.PartialFrameTimeout = -1);
    }

    [Fact(Timeout = 30000)]
    public void AllowedRemoteAddresses_Unparseable_Throws()
    {
        AssertRejected(config => config.AllowedRemoteAddresses = new[] { "127.0.0.2", "not-an-ip" });
        AssertRejected(config => config.AllowedRemoteAddresses = new string[] { null! });
    }

    [Fact(Timeout = 30000)]
    public void Heartbeat_EnabledWithoutPayloadOrProbe_Throws()
    {
        AssertRejected(config => config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, MaxFailures = 1 });
    }

    [Fact(Timeout = 30000)]
    public void Heartbeat_InvalidValues_Throw()
    {
        AssertRejected(config => config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 0, Timeout = 100, MaxFailures = 1, Payload = "PING" });
        AssertRejected(config => config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 0, MaxFailures = 1, Payload = "PING" });
        AssertRejected(config => config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, MaxFailures = 0, Payload = "PING" });
    }

    [Fact(Timeout = 30000)]
    public void Correlation_KeyedWithoutKeyExtractor_Throws()
    {
        AssertRejected(config => config.Correlation = CorrelationMode.Keyed);
    }

    [Fact(Timeout = 30000)]
    public void RequiredSections_MustNotBeNull()
    {
        AssertRejected(config => config.Framing = null!);
        AssertRejected(config => config.Heartbeat = null!);
        AssertRejected(config => config.RestartOnFault = null!);
        AssertRejected(config => config.Socket = null!);
        AssertRejected(config => config.Tls = null!);
    }

    [Fact(Timeout = 30000)]
    public void ReceiveQueueCapacity_NotPositive_Throws()
    {
        AssertRejected(config => config.ReceiveQueueCapacity = 0);
    }

    [Fact(Timeout = 30000)]
    public void Config_NotMutated_ByConstruction()
    {
        var config = new TcpServerConfig
        {
            ListenAddress = "127.0.0.1",
            Port = 5001,
            MaxSessions = 3,
            AllowedRemoteAddresses = new[] { "127.0.0.2" },
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\n" } },
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, MaxFailures = 2, Payload = "PING" },
            RestartOnFault = new ReconnectOptions { Enabled = true, Interval = 50 },
            Socket = new TcpSocketOptions { ReceiveBufferSize = 4096 },
        };
        string before = Describe(config, 2);

        using (new TcpServer(config))
        {
        }

        Assert.Equal(before, Describe(config, 2));
    }

    [Fact(Timeout = 30000)]
    public void LateReplyWindow_BelowMinusOne_Throws()
    {
        AssertRejected(config => config.LateReplyWindow = -2);
    }

    [Fact(Timeout = 30000)]
    public void SharedHealthProbeOnServer_Throws()
    {
        // 所有会话共用的探测无法绑定到单个会话：服务端只接受 HealthProbeFactory。
        var components = new TcpChannelComponents
        {
            HealthProbe = new DelegateProbe(_ => Task.FromResult(CommResult.Success())),
        };

        Assert.ThrowsAny<ArgumentException>(() => new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = 5000 }, null, components));
    }

    [Fact(Timeout = 30000)]
    public void HealthProbe_AndHealthProbeFactory_ThrowOnServer()
    {
        // 两者互斥：同时设置时构造即抛出 ArgumentException（服务端不接受 HealthProbe，因此这里同时覆盖互斥校验）。
        var probe = new DelegateProbe(_ => Task.FromResult(CommResult.Success()));
        var components = new TcpChannelComponents
        {
            HealthProbe = probe,
            HealthProbeFactory = _ => probe,
        };

        Assert.ThrowsAny<ArgumentException>(() => new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = 5000 }, null, components));
    }

    [Fact(Timeout = 30000)]
    public void HealthProbeFactory_Alone_IsAccepted()
    {
        // 只设置工厂时服务端接受配置（心跳未启用，因此不需要负载，也不调用工厂）。
        var components = new TcpChannelComponents
        {
            HealthProbeFactory = _ => new DelegateProbe(_ => Task.FromResult(CommResult.Success())),
        };

        using var server = new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = 5000 }, null, components);

        Assert.Equal(ServerState.Stopped, server.State);
    }

    // 每个非法配置都必须在构造时抛出 ArgumentException 族，并且不改写调用方的对象。
    private static void AssertRejected(Action<TcpServerConfig> mutate)
    {
        var config = new TcpServerConfig { ListenAddress = "127.0.0.1", Port = 5000 };
        string before = Describe(config, 2);
        mutate(config);
        string mutated = Describe(config, 2);

        Assert.ThrowsAny<ArgumentException>(() => new TcpServer(config));
        Assert.Equal(mutated, Describe(config, 2));
        Assert.NotEqual(before, mutated);
    }

    // 以反射列出公开属性的值（嵌套对象展开到给定深度），用于判断构造前后配置是否被改写。
    private static string Describe(object? value, int depth)
    {
        if (value == null)
            return "null";

        if (value is string || value.GetType().IsPrimitive || value is Enum)
            return value.ToString() ?? "null";

        if (value is Array array)
        {
            var items = new List<string>();
            foreach (object? item in array)
                items.Add(Describe(item, 0));

            return "[" + string.Join(",", items) + "]";
        }

        if (depth == 0)
            return value.GetType().Name;

        var parts = new List<string>();
        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            parts.Add(property.Name + "=" + Describe(property.GetValue(value), depth - 1));

        return "{" + string.Join(";", parts) + "}";
    }
}
