namespace Junevy.Communication.Mqtt.Tests;

public sealed class MqttClientOptionsTests
{
    private static MqttClientOptions Valid() => new MqttClientOptions
    {
        Host = "broker.local",
        ClientId = "client-1",
    };

    [Fact]
    public void ValidOptions_Pass()
    {
        var options = Valid();

        MqttOptionsValidator.Validate(options);
    }

    [Fact]
    public void OptionsIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>("options", () => MqttOptionsValidator.Validate(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HostIsMissing_Throws(string? host)
    {
        MqttClientOptions options = Valid();
        options.Host = host;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("Host", error.Message, StringComparison.Ordinal);
        Assert.Equal("options.Host", error.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ClientIdIsMissing_Throws(string? clientId)
    {
        MqttClientOptions options = Valid();
        options.ClientId = clientId;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("ClientId", error.Message, StringComparison.Ordinal);
        Assert.Equal("options.ClientId", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void PortIsOutOfRange_Throws(int port)
    {
        MqttClientOptions options = Valid();
        options.Port = port;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("between 1 and 65535", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(65535)]
    public void KeepAliveAtTheBounds_Passes(int keepAlive)
    {
        MqttClientOptions options = Valid();
        options.KeepAliveSeconds = keepAlive;

        MqttOptionsValidator.Validate(options);

        Assert.Equal(keepAlive, options.KeepAliveSeconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void KeepAliveIsOutOfRange_Throws(int keepAlive)
    {
        MqttClientOptions options = Valid();
        options.KeepAliveSeconds = keepAlive;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("between 0 and 65535", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TimeoutIsNotPositive_Throws(int timeout)
    {
        MqttClientOptions options = Valid();
        options.Timeout = timeout;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("greater than 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectIntervalIsNotPositive_Throws()
    {
        MqttClientOptions options = Valid();
        options.ReconnectInterval = 0;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => MqttOptionsValidator.Validate(options));

        Assert.Equal("options.ReconnectInterval", error.ParamName);
    }

    [Fact]
    public void MaxReconnectIntervalBelowReconnectInterval_Throws()
    {
        MqttClientOptions options = Valid();
        options.ReconnectInterval = 1000;
        options.MaxReconnectInterval = 999;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("must not be smaller than", error.Message, StringComparison.Ordinal);
        Assert.Equal("options.MaxReconnectInterval", error.ParamName);
    }

    [Fact]
    public void UndefinedEnumValue_Throws()
    {
        MqttClientOptions options = Valid();
        options.ProtocolVersion = (MqttProtocolVersion)7;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("ProtocolVersion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UndefinedWillQos_Throws()
    {
        MqttClientOptions options = Valid();
        options.WillTopic = "devices/1/status";
        options.WillQos = (MqttQos)9;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("WillQos", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionExpiryWithoutV500_Throws()
    {
        MqttClientOptions options = Valid();
        options.ProtocolVersion = MqttProtocolVersion.V311;
        options.SessionExpiryInterval = 30;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Equal("options.SessionExpiryInterval", error.ParamName);
    }

    [Fact]
    public void SessionExpiryWithV500_Passes()
    {
        MqttClientOptions options = Valid();
        options.ProtocolVersion = MqttProtocolVersion.V500;
        options.SessionExpiryInterval = 30;

        MqttOptionsValidator.Validate(options);
    }

    [Fact]
    public void PasswordWithoutUserName_Throws()
    {
        MqttClientOptions options = Valid();
        options.Password = "secret";

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Equal("options.UserName", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("devices/+/status")]
    [InlineData("devices/#")]
    public void InvalidWillTopic_Throws(string willTopic)
    {
        MqttClientOptions options = Valid();
        options.WillTopic = willTopic;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Equal("options.WillTopic", error.ParamName);
    }

    [Theory]
    [InlineData("offline", false, MqttQos.AtMostOnce)]
    [InlineData(null, true, MqttQos.AtMostOnce)]
    [InlineData(null, false, MqttQos.ExactlyOnce)]
    public void WillSettingsWithoutWillTopic_Throw(string? payload, bool retain, MqttQos qos)
    {
        MqttClientOptions options = Valid();
        options.WillPayload = payload;
        options.WillRetain = retain;
        options.WillQos = qos;

        var error = Assert.Throws<ArgumentException>(() => MqttOptionsValidator.Validate(options));

        Assert.Contains("WillTopic is null", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoWillSettingsAtAll_Passes()
    {
        // 没有遗嘱主题时，遗嘱正文、保留标志与 QoS 保持默认值是合法的，不能误伤。
        MqttClientOptions options = Valid();

        MqttOptionsValidator.Validate(options);

        Assert.Null(options.WillTopic);
        Assert.Null(options.WillPayload);
        Assert.False(options.WillRetain);
        Assert.Equal(MqttQos.AtMostOnce, options.WillQos);
    }

    [Fact]
    public void Copy_IsIndependentAndCoversEveryProperty()
    {
        MqttClientOptions source = new MqttClientOptions
        {
            Host = "broker.local",
            Port = 8883,
            ClientId = "plc-1",
            UserName = "device",
            Password = "s3cret",
            ProtocolVersion = MqttProtocolVersion.V500,
            CleanSession = false,
            SessionExpiryInterval = 3600,
            KeepAliveSeconds = 30,
            Timeout = 8000,
            UseTls = true,
            AllowUntrustedCertificates = true,
            AutoReconnect = false,
            ReconnectInterval = 2000,
            MaxReconnectInterval = 60000,
            WillTopic = "devices/plc-1/status",
            WillPayload = "offline",
            WillQos = MqttQos.ExactlyOnce,
            WillRetain = true,
        };

        MqttClientOptions copy = source.Copy();

        // 逐项比对：每个属性都进了副本（默认值不同的那几项也已显式覆盖）。
        foreach (System.Reflection.PropertyInfo property in typeof(MqttClientOptions).GetProperties())
        {
            object? expected = property.GetValue(source);
            object? actual = property.GetValue(copy);
            Assert.True(Equals(expected, actual), $"{property.Name}: expected {expected ?? "null"} but the copy has {actual ?? "null"}.");
        }

        Assert.NotSame(source, copy);
        source.Port = 1;
        Assert.Equal(8883, copy.Port);
    }

    [Fact]
    public void CompleteWill_Passes()
    {
        MqttClientOptions options = Valid();
        options.WillTopic = "devices/1/status";
        options.WillPayload = "offline";
        options.WillRetain = true;
        options.WillQos = MqttQos.AtLeastOnce;

        MqttOptionsValidator.Validate(options);
    }
}