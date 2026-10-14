using Junevy.Communication.Mqtt.Tests.TestSupport;
using MQTTnet;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 连接用例共用的构造：选项、客户端与 Broker 都由这里给出默认值，用例只改自己关心的那一项。
/// </summary>
internal static class MqttTestOptions
{
    public const int TimeoutMs = 5000;

    public static MqttClientOptions Valid(int port, string clientId = "client-1", MqttProtocolVersion version = MqttProtocolVersion.V311) =>
        new MqttClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            ClientId = clientId,
            ProtocolVersion = version,
            Timeout = TimeoutMs,
            KeepAliveSeconds = 0,
        };

    /// <summary>打开自动重连，并把退避调到几十毫秒——重连用例不要真的等几秒（计划第 1 节第 7 条）。</summary>
    public static MqttClientOptions Reconnecting(this MqttClientOptions options, int intervalMs = 40, int maxIntervalMs = 200)
    {
        options.AutoReconnect = true;
        options.ReconnectInterval = intervalMs;
        options.MaxReconnectInterval = maxIntervalMs;
        return options;
    }
}

internal static class MqttClientExtensions
{
    /// <summary>连接并断言成功。</summary>
    public static async Task ConnectAndAssertAsync(this MqttClient client)
    {
        Junevy.Communication.Core.Results.CommResult result = await client.ConnectAsync();
        Assert.True(result.IsSuccess, $"ConnectAsync failed: {result}");
    }
}