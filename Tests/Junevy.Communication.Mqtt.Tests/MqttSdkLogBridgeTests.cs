using Junevy.Communication.Mqtt.Sdk;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;
using SdkLogger = MQTTnet.Diagnostics.Logger;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// SDK 日志桥接的守卫（P6-C10）：桥到同一个 <see cref="ILoggerFactory"/>，类别固定为
/// <c>Junevy.Communication.Mqtt.Sdk</c>，且只放行 Warning 及以上——无法逐条确认 SDK 的每一条日志都不含
/// 凭据与报文内容，因此按 P6-C10 的保守做法处理。
/// </summary>
public sealed class MqttSdkLogBridgeTests
{
    private const string Category = "Junevy.Communication.Mqtt.Sdk";

    [Theory]
    [InlineData(SdkLogger.MqttNetLogLevel.Warning, LogLevel.Warning)]
    [InlineData(SdkLogger.MqttNetLogLevel.Error, LogLevel.Error)]
    public void Publish_BridgesWarningAndAbove(SdkLogger.MqttNetLogLevel level, LogLevel expected)
    {
        var logger = new TestLogger();
        var bridge = new MqttSdkLogBridge(logger);

        bridge.Publish(level, "MqttClient", "something happened", Array.Empty<object>(), null);

        TestLogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(expected, entry.Level);
        Assert.Equal(Category, entry.CategoryName);
        Assert.Contains("MqttClient", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SdkLogger.MqttNetLogLevel.Verbose)]
    [InlineData(SdkLogger.MqttNetLogLevel.Info)]
    public void Publish_DropsAnythingBelowWarning(SdkLogger.MqttNetLogLevel level)
    {
        var logger = new TestLogger();
        var bridge = new MqttSdkLogBridge(logger);

        bridge.Publish(level, "MqttClient", "verbose detail", Array.Empty<object>(), null);

        Assert.Empty(logger.Entries);
        Assert.Equal(logger.IsEnabled(LogLevel.Warning), bridge.IsEnabled);
    }

    [Fact]
    public void Publish_FormatsTheSdkTemplate()
    {
        var logger = new TestLogger();
        var bridge = new MqttSdkLogBridge(logger);

        bridge.Publish(
            SdkLogger.MqttNetLogLevel.Warning,
            "MqttClient",
            "topic {0} rejected",
            new object[] { "a/b" },
            null);

        Assert.Contains("topic a/b rejected", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_SurvivesATemplateWithoutItsArguments()
    {
        var logger = new TestLogger();
        var bridge = new MqttSdkLogBridge(logger);

        bridge.Publish(SdkLogger.MqttNetLogLevel.Warning, "MqttClient", "topic {0} rejected", Array.Empty<object>(), null);

        Assert.Equal("[MqttClient] topic {0} rejected", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void Constructor_NullLoggerFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>("loggerFactory", () => new MqttSdkLogBridge(null!));
    }
}