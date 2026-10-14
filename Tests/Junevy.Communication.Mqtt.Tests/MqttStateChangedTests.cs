using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttStateChangedTests
{
    [Fact]
    public async Task StateChanged_ReportsEveryTransitionInOrderWithItsReason()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        var transitions = new List<(MqttConnectionState Previous, MqttConnectionState Current, string? Reason)>();
        client.StateChanged += (_, args) =>
        {
            lock (transitions)
            {
                transitions.Add((args.Previous, args.Current, args.Reason));
            }
        };

        await client.ConnectAndAssertAsync();
        await client.DisconnectAsync();

        lock (transitions)
        {
            Assert.Equal(
                new[]
                {
                    (MqttConnectionState.Disconnected, MqttConnectionState.Connecting, (string?)null),
                    (MqttConnectionState.Connecting, MqttConnectionState.Connected, null),
                    (MqttConnectionState.Connected, MqttConnectionState.Disconnected, "The client was disconnected by the application."),
                },
                transitions);
        }
    }

    [Fact]
    public async Task StateChanged_AFailedConnectCarriesTheReason()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).WithCredentials("right", "right");
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.UserName = "wrong";
        options.Password = "wrong";
        using var client = MqttClient.Create(options);

        var transitions = new List<MqttConnectionStateChangedEventArgs>();
        client.StateChanged += (_, args) =>
        {
            lock (transitions)
            {
                transitions.Add(args);
            }
        };

        Assert.False((await client.ConnectAsync()).IsSuccess);

        lock (transitions)
        {
            MqttConnectionStateChangedEventArgs failure = Assert.Single(transitions, item => item.Current == MqttConnectionState.Disconnected);
            Assert.False(string.IsNullOrEmpty(failure.Reason));
        }
    }

    [Fact]
    public async Task StateChanged_ASubscriberThatThrowsDoesNotBreakTheTransition()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        var seenBySecondSubscriber = new List<MqttConnectionState>();
        client.StateChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
        client.StateChanged += (_, args) =>
        {
            lock (seenBySecondSubscriber)
            {
                seenBySecondSubscriber.Add(args.Current);
            }
        };

        await client.ConnectAndAssertAsync();

        Assert.Equal(MqttConnectionState.Connected, client.State);
        lock (seenBySecondSubscriber)
        {
            Assert.Equal(
                new[] { MqttConnectionState.Connecting, MqttConnectionState.Connected },
                seenBySecondSubscriber);
        }
    }

    [Fact]
    public async Task StateChanged_AFailingSubscriberIsLoggedAsError()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        var logger = new TestLogger();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port), logger);
        client.StateChanged += (_, _) => throw new InvalidOperationException("subscriber failure");

        await client.ConnectAndAssertAsync();

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }
}