using System.IO.Ports;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Serial.Tests.SerialTestHelpers;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 串口配置的默认值、校验与快照语义（计划 14.3 的 <c>Config_Invalid_Throws</c>，以及 D5 的"不改写调用者对象"）。
/// 本类只构造通道、不连接，因此不会打开任何端口。
/// </summary>
public sealed class SerialChannelConfigTests
{
    [Fact(Timeout = 30000)]
    public void DefaultConfig_MatchesDesign()
    {
        // 设计文档第 8 节与 D11 的默认值。
        var config = new SerialChannelConfig();

        Assert.Equal("COM1", config.PortName);
        Assert.Equal(9600, config.BaudRate);
        Assert.Equal(8, config.DataBits);
        Assert.Equal(Parity.None, config.Parity);
        Assert.Equal(StopBits.One, config.StopBits);
        Assert.Equal(Handshake.None, config.Handshake);
        Assert.False(config.DtrEnable);
        Assert.False(config.RtsEnable);
        Assert.Equal(4096, config.ReadBufferSize);
        Assert.Equal(2048, config.WriteBufferSize);
        Assert.Equal(2000, config.OpenTimeout);
        Assert.Equal(5000, config.HandshakeTimeout);
        Assert.Equal(1000, config.DisconnectTimeout);
        Assert.Equal(2000, config.SendTimeout);
        Assert.Equal(2000, config.RequestTimeout);
        Assert.Equal(0, config.IdleTimeout);
        Assert.Equal(0, config.PartialFrameTimeout);
        Assert.Equal(-1, config.LateReplyWindow);
        Assert.Equal(FramingMode.IdleGap, config.Framing.Mode);
        Assert.Equal(20, config.Framing.GapTimeout);
        Assert.Equal(CorrelationMode.Sequential, config.Correlation);
        Assert.False(config.Heartbeat.Enabled);
        Assert.False(config.Reconnect.Enabled);
        Assert.Equal(1024, config.ReceiveQueueCapacity);
        Assert.Equal(QueueFullMode.DropOldest, config.QueueFullMode);
    }

    [Theory(Timeout = 30000)]
    [InlineData("PortName", "")]
    [InlineData("PortName", "   ")]
    [InlineData("PortName", null)]
    [InlineData("BaudRate", 0)]
    [InlineData("BaudRate", -9600)]
    [InlineData("DataBits", 4)]
    [InlineData("DataBits", 9)]
    [InlineData("Parity", 99)]
    [InlineData("StopBits", 0)]
    [InlineData("StopBits", 99)]
    [InlineData("Handshake", 99)]
    [InlineData("ReadBufferSize", 0)]
    [InlineData("ReadBufferSize", 4095)]
    [InlineData("WriteBufferSize", -1)]
    [InlineData("WriteBufferSize", 3)]
    [InlineData("OpenTimeout", 0)]
    [InlineData("HandshakeTimeout", 0)]
    [InlineData("HandshakeTimeout", -1)]
    [InlineData("DisconnectTimeout", -1)]
    [InlineData("SendTimeout", -1)]
    [InlineData("RequestTimeout", 0)]
    [InlineData("IdleTimeout", -1)]
    [InlineData("PartialFrameTimeout", -1)]
    [InlineData("LateReplyWindow", -2)]
    [InlineData("ReceiveQueueCapacity", 0)]
    [InlineData("Framing", null)]
    [InlineData("Heartbeat.Payload", null)]
    [InlineData("Reconnect", null)]
    public void Config_Invalid_Throws(string property, object? value)
    {
        SerialChannelConfig config = CreateValidConfig();
        ApplyInvalid(config, property, value);

        Assert.ThrowsAny<ArgumentException>(() => new SerialChannel(config).Dispose());
    }

    [Fact(Timeout = 30000)]
    public void Config_NotMutated()
    {
        var config = new SerialChannelConfig
        {
            PortName = "COM4",
            BaudRate = 19200,
            DataBits = 7,
            Parity = Parity.Even,
            StopBits = StopBits.OnePointFive,
            Handshake = Handshake.RequestToSend,
            DtrEnable = true,
            RtsEnable = true,
            ReadBufferSize = 8192,
            WriteBufferSize = 4096,
            OpenTimeout = 1500,
            HandshakeTimeout = 4000,
            DisconnectTimeout = 700,
            SendTimeout = 2500,
            RequestTimeout = 1200,
            IdleTimeout = 9000,
            PartialFrameTimeout = 150,
            LateReplyWindow = 250,
            Framing = new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = 35 },
            Correlation = CorrelationMode.Sequential,
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 1000, Timeout = 500, MaxFailures = 4, OnlyWhenIdle = false, Payload = "PING", ExpectedReply = "PONG" },
            Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 250, MaxInterval = 5000, MaxAttempts = 7, OnInitialFailure = true },
            ReceiveQueueCapacity = 64,
            QueueFullMode = QueueFullMode.DropNewest,
        };

        string before = Describe(config);
        using (var channel = new SerialChannel(config))
        {
            Assert.Same(config, channel.Config);
        }

        Assert.Equal(before, Describe(config));
    }

    [Fact(Timeout = 30000)]
    public void HeartbeatWithoutExpectedReply_Throws()
    {
        // 串口写入几乎总是成功：内置探测必须指定期望的应答，否则对端沉默无法被发现（设计 5.4、D14）。
        SerialChannelConfig config = CreateValidConfig();
        config.Heartbeat = new HeartbeatOptions { Enabled = true, Payload = "PING", Interval = 100, Timeout = 100 };

        ArgumentException exception = Assert.ThrowsAny<ArgumentException>(() => new SerialChannel(config).Dispose());
        Assert.Contains("ExpectedReply", exception.Message);
    }

    [Fact(Timeout = 30000)]
    public void HeartbeatWithoutExpectedReply_WithHealthProbe_Constructs()
    {
        // 代码级探测替代内置探测时，不需要 ExpectedReply；此时校验不应拒绝配置。
        SerialChannelConfig config = CreateValidConfig();
        config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100 };
        var components = new ChannelComponents { HealthProbe = new NoopProbe() };

        using var channel = new SerialChannel(config, null, components);

        Assert.Equal("COM3", channel.Name);
    }

    [Fact(Timeout = 30000)]
    public void HealthProbeFactory_ReplacesExpectedReplyRequirement()
    {
        // 探测工厂替代内置探测：没有 ExpectedReply 与 Payload 时构造仍然成功（工厂在打开端口之后才调用，构造不打开端口）。
        SerialChannelConfig config = CreateValidConfig();
        config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100 };
        var components = new ChannelComponents { HealthProbeFactory = _ => new NoopProbe() };

        using var channel = new SerialChannel(config, null, components);

        Assert.Equal("COM3", channel.Name);
    }

    [Fact(Timeout = 30000)]
    public void HealthProbe_AndHealthProbeFactory_Throws()
    {
        SerialChannelConfig config = CreateValidConfig();
        var probe = new NoopProbe();
        var components = new ChannelComponents { HealthProbe = probe, HealthProbeFactory = _ => probe };

        Assert.Throws<ArgumentException>(() => new SerialChannel(config, null, components));
    }

    private sealed class NoopProbe : IHealthProbe
    {
        public Task<CommResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(CommResult.Success());
    }

    // 合法的基准配置：每个用例只改动一处。
    private static SerialChannelConfig CreateValidConfig()
        => new SerialChannelConfig
        {
            PortName = "COM3",
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } },
            Heartbeat = new HeartbeatOptions { Enabled = false },
        };

    // 按属性名把配置改成非法值。
    private static void ApplyInvalid(SerialChannelConfig config, string property, object? value)
    {
        switch (property)
        {
            case "PortName":
                config.PortName = (string)value!;
                break;
            case "BaudRate":
                config.BaudRate = (int)value!;
                break;
            case "DataBits":
                config.DataBits = (int)value!;
                break;
            case "Parity":
                config.Parity = (Parity)(int)value!;
                break;
            case "StopBits":
                config.StopBits = (StopBits)(int)value!;
                break;
            case "Handshake":
                config.Handshake = (Handshake)(int)value!;
                break;
            case "ReadBufferSize":
                config.ReadBufferSize = (int)value!;
                break;
            case "WriteBufferSize":
                config.WriteBufferSize = (int)value!;
                break;
            case "OpenTimeout":
                config.OpenTimeout = (int)value!;
                break;
            case "SendTimeout":
                config.SendTimeout = (int)value!;
                break;
            case "RequestTimeout":
                config.RequestTimeout = (int)value!;
                break;
            case "IdleTimeout":
                config.IdleTimeout = (int)value!;
                break;
            case "PartialFrameTimeout":
                config.PartialFrameTimeout = (int)value!;
                break;
            case "LateReplyWindow":
                config.LateReplyWindow = (int)value!;
                break;
            case "HandshakeTimeout":
                config.HandshakeTimeout = (int)value!;
                break;
            case "DisconnectTimeout":
                config.DisconnectTimeout = (int)value!;
                break;
            case "ReceiveQueueCapacity":
                config.ReceiveQueueCapacity = (int)value!;
                break;
            case "Framing":
                config.Framing = null!;
                break;
            case "Heartbeat.Payload":
                // 启用心跳却没有探测负载：由基类的 StreamClientOptions 拒绝。
                config.Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 100, Payload = null };
                break;
            case "Reconnect":
                config.Reconnect = null!;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown property.");
        }
    }
}
