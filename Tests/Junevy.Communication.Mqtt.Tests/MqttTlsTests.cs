using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttTlsTests
{
    [Fact]
    public async Task Connect_UntrustedCertificate_ReportsAuthenticationFailed()
    {
        await using TestBroker broker = await TestBroker.StartTlsAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.Host = "localhost";
        options.UseTls = true;
        options.AllowUntrustedCertificates = false;
        using var client = MqttClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Connect_AllowingUntrustedCertificates_ConnectsAndWarns()
    {
        await using TestBroker broker = await TestBroker.StartTlsAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.Host = "localhost";
        options.UseTls = true;
        options.AllowUntrustedCertificates = true;
        var logger = new TestLogger();
        using var client = MqttClient.Create(options, logger);

        await client.ConnectAndAssertAsync();

        Assert.Equal(MqttConnectionState.Connected, client.State);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("AllowUntrustedCertificates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connect_TlsToAPlainBroker_Fails()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.UseTls = true;
        using var client = MqttClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }
}