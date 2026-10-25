using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Mqtt;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Mqtt;
using Junevy.Communication.Testing.Servers;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Mqtt;

/// <summary>
/// MQTT 页与进程内 Broker（Task 16 的 <see cref="TestBroker"/>）的端到端测试（计划 Task 17 第 2 条）：订阅、发布、接收（文本与十六进制）；
/// MQTT 5 属性的往返；V3.1.1 上发布带属性的消息返回 InvalidRequest 且不发送；遗嘱参数的映射；意外断线后的重连与订阅恢复；错误分类。
/// </summary>
public sealed class MqttEndToEndTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static MqttSessionViewModel CreatePage(ManualUiDispatcher dispatcher, int port, string clientId)
    {
        var page = new MqttSessionViewModel(ProtocolCatalog.Get(ProtocolKind.Mqtt), FakeServices.Create(dispatcher));
        page.Host = "127.0.0.1";
        page.Port = port;
        page.ClientId = clientId;
        return page;
    }

    [Fact]
    public async Task Subscribe_Publish_Receive_TextAndHexPayloads()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var broker = await TestBroker.StartAsync();
        await using var subscriber = CreatePage(dispatcher, broker.Port, "sub-text");
        await using var publisher = CreatePage(dispatcher, broker.Port, "pub-text");
        await subscriber.ConnectCommand.ExecuteAsync(null);
        await publisher.ConnectCommand.ExecuteAsync(null);

        subscriber.NewTopicFilter = "sensors/#";
        subscriber.NewSubscribeQos = MqttQos.AtLeastOnce;
        await subscriber.SubscribeCommand.ExecuteAsync(null);
        Assert.Equal(("sensors/#", "QoS 1"), (subscriber.Subscriptions[0].TopicFilter, subscriber.Subscriptions[0].GrantedText));

        publisher.PublishTopic = "sensors/temp";
        publisher.PublishPayload = "21.5";
        publisher.PublishQos = MqttQos.AtLeastOnce;
        await publisher.PublishCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => subscriber.ReceivedMessages.Count == 1, Wait, dispatcher.RunPending);
        MqttReceivedRow text = subscriber.ReceivedMessages[0];
        Assert.Equal(("sensors/temp", "QoS 1", "否", "21.5", "32 31 2E 35"), (text.Topic, text.Qos, text.Retain, text.PayloadText, text.PayloadHex));

        publisher.PublishPayloadAsHex = true;
        publisher.PublishPayload = "00 FF 10";
        await publisher.PublishCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => subscriber.ReceivedMessages.Count == 2, Wait, dispatcher.RunPending);
        Assert.Equal("00 FF 10", subscriber.ReceivedMessages[0].PayloadHex);
    }

    [Fact]
    public async Task Mqtt5Properties_RoundTrip_ThroughTheBroker()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var broker = await TestBroker.StartAsync();
        await using var subscriber = CreatePage(dispatcher, broker.Port, "sub-v5");
        await using var publisher = CreatePage(dispatcher, broker.Port, "pub-v5");
        subscriber.ProtocolVersion = MqttProtocolVersion.V500;
        publisher.ProtocolVersion = MqttProtocolVersion.V500;
        await subscriber.ConnectCommand.ExecuteAsync(null);
        await publisher.ConnectCommand.ExecuteAsync(null);
        subscriber.NewTopicFilter = "rpc/#";
        await subscriber.SubscribeCommand.ExecuteAsync(null);

        publisher.PublishTopic = "rpc/call";
        publisher.PublishPayload = "{}";
        publisher.ContentType = "application/json";
        publisher.ResponseTopic = "rpc/reply";
        publisher.CorrelationDataHex = "0A 0B";
        publisher.MessageExpiryText = "60";
        publisher.NewUserPropertyName = "tenant";
        publisher.NewUserPropertyValue = "acme";
        publisher.AddUserPropertyCommand.Execute(null);
        await publisher.PublishCommand.ExecuteAsync(null);

        await Waiting.UntilAsync(() => subscriber.ReceivedMessages.Count == 1, Wait, dispatcher.RunPending);
        string properties = subscriber.ReceivedMessages[0].Properties;
        Assert.Contains("ContentType=application/json", properties);
        Assert.Contains("ResponseTopic=rpc/reply", properties);
        Assert.Contains("CorrelationData=0A 0B", properties);
        Assert.Contains("Expiry=", properties);
        Assert.Contains("tenant=acme", properties);
    }

    [Fact]
    public async Task Mqtt5Properties_OnAV311Connection_ReturnInvalidRequest_AndNothingIsSent()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var broker = await TestBroker.StartAsync();
        await using var page = CreatePage(dispatcher, broker.Port, "v311-props");
        await page.ConnectCommand.ExecuteAsync(null);
        page.PublishTopic = "rpc/call";
        page.PublishPayload = "{}";
        page.ContentType = "application/json";

        await page.PublishCommand.ExecuteAsync(null);

        OperationRecord record = Assert.Single(page.Operations, r => r.Operation == "发布");
        Assert.Equal((false, CommErrorKind.InvalidRequest), (record.Succeeded, record.ErrorKind));
        Assert.StartsWith("请求无效", page.PublishMessage);
        Assert.Empty(broker.Published);
    }

    [Fact]
    public async Task WillSettings_ArePublishedByTheBroker_WhenTheClientIsDropped()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var broker = await TestBroker.StartAsync();
        await using var watcher = CreatePage(dispatcher, broker.Port, "watcher");
        await using var device = CreatePage(dispatcher, broker.Port, "device-a");
        await watcher.ConnectCommand.ExecuteAsync(null);
        watcher.NewTopicFilter = "devices/a/status";
        await watcher.SubscribeCommand.ExecuteAsync(null);

        device.WillTopic = "devices/a/status";
        device.WillPayload = "offline";
        device.WillQos = MqttQos.AtLeastOnce;
        device.WillRetain = true;
        device.AutoReconnect = false;
        await device.ConnectCommand.ExecuteAsync(null);

        await broker.DisconnectClientAsync("device-a");

        // 实时送达的副本按 MQTT 3.1.1 的规定 Retain 为 0；保留标志由 Broker 保存下来，见下面的晚到订阅者。
        await Waiting.UntilAsync(() => watcher.ReceivedMessages.Count == 1, Wait, dispatcher.RunPending);
        MqttReceivedRow will = watcher.ReceivedMessages[0];
        Assert.Equal(("devices/a/status", "offline", "否"), (will.Topic, will.PayloadText, will.Retain));

        await using var late = CreatePage(dispatcher, broker.Port, "late");
        await late.ConnectCommand.ExecuteAsync(null);
        late.NewTopicFilter = "devices/a/status";
        await late.SubscribeCommand.ExecuteAsync(null);
        await Waiting.UntilAsync(() => late.ReceivedMessages.Count == 1, Wait, dispatcher.RunPending);
        Assert.Equal(("offline", "是"), (late.ReceivedMessages[0].PayloadText, late.ReceivedMessages[0].Retain));

        // 自动重连关闭：意外断线后页签是故障，保留库给出的原因，并回到未连接的文字。
        await Waiting.UntilAsync(() => device.State == SessionState.Faulted, Wait, dispatcher.RunPending);
        Assert.Equal(("未连接", false), (device.MqttStateText, string.IsNullOrEmpty(device.StateReason)));
    }

    [Fact]
    public async Task AnUnexpectedDrop_ShowsReconnecting_AndRestoresTheSubscription()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var broker = await TestBroker.StartAsync();
        await using var device = CreatePage(dispatcher, broker.Port, "device-r");
        device.ReconnectInterval = 100;
        device.MaxReconnectInterval = 500;
        await device.ConnectCommand.ExecuteAsync(null);
        device.NewTopicFilter = "cmd/#";
        device.NewSubscribeQos = MqttQos.AtLeastOnce;
        await device.SubscribeCommand.ExecuteAsync(null);

        var states = new List<SessionState>();
        device.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(device.State))
            {
                states.Add(device.State);
            }
        };

        await broker.DisconnectClientAsync("device-r");

        await Waiting.UntilAsync(
            () => states.Contains(SessionState.Reconnecting) && device.State == SessionState.Connected,
            Wait,
            dispatcher.RunPending);
        Assert.Equal("cmd/#", Assert.Single(device.Subscriptions).TopicFilter);
        // 同一个过滤器在 Broker 上被订阅了两次：连接时一次，重连后恢复一次。
        Assert.Equal(2, broker.Subscriptions.Count(subscription => subscription.Topic == "cmd/#"));
    }

    [Fact]
    public async Task ConnectToAClosedPort_IsConnectionClosed_AndStaysNotConnected()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = CreatePage(dispatcher, FreePort.Get(), "nobody");

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal((false, CommErrorKind.ConnectionClosed), (record.Succeeded, record.ErrorKind));
    }

    [Fact]
    public async Task WrongCredentials_AreAuthenticationFailed_WithTheBrokersReasonCode()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var broker = await TestBroker.StartAsync();
        broker.WithCredentials("operator", "right");
        await using var page = CreatePage(dispatcher, broker.Port, "auth");
        page.UserName = "operator";
        page.Password = "wrong";

        await page.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.NotConnected, page.State);
        OperationRecord record = Assert.Single(page.Operations);
        Assert.Equal((false, CommErrorKind.AuthenticationFailed), (record.Succeeded, record.ErrorKind));
        Assert.NotNull(record.ProtocolCode);
    }
}
