using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttLoggingTests
{
    private const string Password = "top-secret-password";

    [Fact]
    public async Task Connect_NeverLogsThePasswordAtAnyLevel()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).WithCredentials("user", Password);
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.UserName = "user";
        options.Password = Password;
        var logger = new TestLogger();
        using var client = MqttClient.Create(options, logger);

        await client.ConnectAndAssertAsync();
        await client.DisconnectAsync();
        client.Dispose();

        AssertNoPassword(logger);
    }

    [Fact]
    public async Task Connect_AFailedConnectStillNeverLogsThePassword()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).WithCredentials("other", "other");
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.UserName = "user";
        options.Password = Password;
        var logger = new TestLogger();
        using var client = MqttClient.Create(options, logger);

        await client.ConnectAsync();

        AssertNoPassword(logger);
    }

    // 记录器开到 Trace 级，并且包含经 Sdk/ 桥接过来的 SDK 日志（Q40）。
    private static void AssertNoPassword(TestLogger logger)
    {
        foreach (TestLogEntry entry in logger.Entries)
        {
            bool inMessage = entry.Message.Contains(Password, StringComparison.Ordinal);
            bool inException = entry.Exception?.Message.Contains(Password, StringComparison.Ordinal) == true;
            Assert.False(inMessage || inException, $"[{entry.CategoryName}/{entry.Level}] {entry.Message} / {entry.Exception}");
        }
    }
}