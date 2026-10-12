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

    // P61-D4：AllowUntrustedCertificates 同时接受主机名不符（SDK 的行为由这条用例钉住，P6-C14）。
    [Fact]
    public async Task Connect_AllowingUntrustedCertificates_AlsoAcceptsAHostNameMismatch()
    {
        using TestCertificate certificate = TestCertificates.CreateSelfSignedFor("broker.invalid");
        await using TestBroker broker = await TestBroker.StartTlsAsync(certificate);
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.Host = "localhost";
        options.UseTls = true;
        options.AllowUntrustedCertificates = true;
        using var client = MqttClient.Create(options);

        await client.ConnectAndAssertAsync();

        Assert.Equal(MqttConnectionState.Connected, client.State);
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