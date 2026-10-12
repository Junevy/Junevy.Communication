using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Microsoft.Extensions.Logging;
using SdkClient = MQTTnet;
using SdkPackets = MQTTnet.Packets;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// 对 SDK 客户端的包装。对外只暴露本库的类型与 Core 的 <see cref="CommResult"/>（P6-C4）：
/// 主题、原因码、异常等 SDK 概念在这里就地翻译掉，别处看不到 SDK（P6-C5）。
/// </summary>
internal sealed class MqttSdkClient : IAsyncDisposable
{
    private readonly SdkClient.IMqttClient client;
    private readonly MqttClientOptions options;
    private readonly ILogger logger;
    private bool disposed;

    public MqttSdkClient(MqttClientOptions options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        // 选项的快照在这里取：复制一份，之后本库不再读调用方的对象（P6-C9）。
        this.options = options.Copy();
        logger = loggerFactory.CreateLogger<MqttClient>();
        client = new SdkClient.MqttClientFactory(new MqttSdkLogBridge(loggerFactory)).CreateMqttClient();
        client.ConnectedAsync += OnConnectedAsync;
        client.DisconnectedAsync += OnDisconnectedAsync;
        client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    /// <summary>SDK 报告已连接（含重连成功）。</summary>
    public event Func<Task>? Connected;

    /// <summary>SDK 报告断开。参数为"断开前是否已连接"与"原因"。</summary>
    public event Action<bool, string?>? Disconnected;

    /// <summary>收到一条报文；正文已在 <see cref="MqttSdkMessages"/> 里复制过。</summary>
    public event Action<MqttMessage>? MessageReceived;

    /// <summary>当前是否已连接。</summary>
    public bool IsConnected => client.IsConnected;

    /// <summary>尝试连接一次。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果与 Broker 是否保留了会话；SDK 的异常就地翻译成结果，不逃出本方法（P6-C8）。</returns>
    public async Task<(CommResult Result, bool SessionPresent)> ConnectAsync(CancellationToken cancellationToken)
    {
        // MQTTnet 把"连接被中止"抛成笼统的通信异常，类型里读不出是超时还是调用方取消，
        // 因此自己按选项里的 Timeout 开一个作用域，分类时以它为准（P61-D4）。
        using TimeoutScope scope = TimeoutScope.Start(options.Timeout, cancellationToken);

        try
        {
            SdkClient.MqttClientConnectResult result = await client
                .ConnectAsync(MqttSdkOptions.Build(options), scope.Token)
                .ConfigureAwait(false);

            return (MqttErrorMap.ConnectOutcome(result.ResultCode), result.IsSessionPresent);
        }
        catch (Exception exception)
        {
            return (MqttErrorMap.Runtime(exception, scope), false);
        }
    }

    /// <summary>主动断开（发送 DISCONNECT）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已断开时成功；异常就地翻译成结果。</returns>
    public async Task<CommResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
        {
            return CommResult.Success();
        }

        try
        {
            await client.DisconnectAsync(
                new SdkClient.MqttClientDisconnectOptionsBuilder()
                    .WithReason(SdkClient.MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build(),
                cancellationToken).ConfigureAwait(false);

            return CommResult.Success();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 尽力而为：断开失败也要继续释放，因此只记 Warning 并把异常翻译成结果（P6-C8）。
            logger.LogWarning(exception, "The MQTT client could not send DISCONNECT cleanly.");
            return MqttErrorMap.Runtime(exception);
        }
    }

    /// <summary>发布一条消息。QoS 0 写出即成功，QoS 1 等 PUBACK，QoS 2 等 PUBCOMP（P61-D6）。</summary>
    /// <param name="message">本库消息；调用方已校验过。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发布结果；SDK 的异常就地翻译成结果，不重发。</returns>
    public async Task<CommResult> PublishAsync(MqttMessage message, CancellationToken cancellationToken)
    {
        using TimeoutScope scope = TimeoutScope.Start(options.Timeout, cancellationToken);

        try
        {
            SdkClient.MqttClientPublishResult result = await client
                .PublishAsync(MqttSdkMessages.ToSdk(message), scope.Token)
                .ConfigureAwait(false);

            return MqttErrorMap.PublishOutcome(result.ReasonCode);
        }
        catch (Exception exception)
        {
            return MqttErrorMap.Runtime(exception, scope);
        }
    }

    /// <summary>订阅一个主题过滤器。</summary>
    /// <param name="topicFilter">主题过滤器；调用方已校验过。</param>
    /// <param name="qos">请求的 QoS。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Broker 授予的 QoS，或失败原因。</returns>
    public async Task<CommResult<MqttQos>> SubscribeAsync(string topicFilter, MqttQos qos, CancellationToken cancellationToken)
    {
        using TimeoutScope scope = TimeoutScope.Start(options.Timeout, cancellationToken);

        SdkPackets.MqttTopicFilter filter = new SdkClient.MqttTopicFilterBuilder()
            .WithTopic(topicFilter)
            .WithQualityOfServiceLevel(MqttSdkQos.ToSdk(qos))
            .Build();

        try
        {
            SdkClient.MqttClientSubscribeResult result = await client
                .SubscribeAsync(new SdkClient.MqttClientSubscribeOptionsBuilder().WithTopicFilter(filter).Build(), scope.Token)
                .ConfigureAwait(false);

            return SubscribeOutcome(result);
        }
        catch (Exception exception)
        {
            return MqttErrorMap.Runtime(exception, scope).As<MqttQos>();
        }
    }

    /// <summary>退订一个主题过滤器。</summary>
    /// <param name="topicFilter">主题过滤器；调用方已校验过。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>退订结果。</returns>
    public async Task<CommResult> UnsubscribeAsync(string topicFilter, CancellationToken cancellationToken)
    {
        using TimeoutScope scope = TimeoutScope.Start(options.Timeout, cancellationToken);

        try
        {
            SdkClient.MqttClientUnsubscribeResult result = await client
                .UnsubscribeAsync(new SdkClient.MqttClientUnsubscribeOptionsBuilder().WithTopicFilter(topicFilter).Build(), scope.Token)
                .ConfigureAwait(false);

            return UnsubscribeOutcome(result);
        }
        catch (Exception exception)
        {
            return MqttErrorMap.Runtime(exception, scope);
        }
    }

    /// <summary>尽力断开后释放 SDK 客户端，等待时间不超过选项里的 <c>Timeout</c>（P61-D11）。</summary>
    /// <returns>释放完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        client.ConnectedAsync -= OnConnectedAsync;
        client.DisconnectedAsync -= OnDisconnectedAsync;
        client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;

        using var deadline = new CancellationTokenSource(options.Timeout);
        try
        {
            await client.DisconnectAsync(new SdkClient.MqttClientDisconnectOptions(), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The MQTT client was released without a clean DISCONNECT.");
        }

        client.Dispose();
    }

    private Task OnConnectedAsync(SdkClient.MqttClientConnectedEventArgs args)
    {
        // 事件回调里不能抛，也不能在这里重连（P61-D9）：重连由本包的后台循环负责。
        Connected?.Invoke();
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(SdkClient.MqttClientDisconnectedEventArgs args)
    {
        // ReasonString 常常是 null（Broker 只给了原因码），回退到原因码本身，保证 StateChanged 的 Reason 非空。
        string? reason = args.ReasonString ?? args.Reason.ToString();
        Disconnected?.Invoke(args.ClientWasConnected, reason);
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(SdkClient.MqttApplicationMessageReceivedEventArgs args)
    {
        MessageReceived?.Invoke(MqttSdkMessages.FromSdk(args.ApplicationMessage));
        return Task.CompletedTask;
    }

    // SUBACK 逐项给结果码；我们一次只订阅一个过滤器，因此取第一项。
    private static CommResult<MqttQos> SubscribeOutcome(SdkClient.MqttClientSubscribeResult result)
    {
        SdkClient.MqttClientSubscribeResultItem item = result.Items.FirstOrDefault()
            ?? throw new InvalidOperationException("The broker answered SUBACK without any result item.");

        return MqttErrorMap.SubscribeOutcome(item.ResultCode);
    }

    private static CommResult UnsubscribeOutcome(SdkClient.MqttClientUnsubscribeResult result)
    {
        SdkClient.MqttClientUnsubscribeResultItem item = result.Items.FirstOrDefault()
            ?? throw new InvalidOperationException("The broker answered UNSUBACK without any result item.");

        return MqttErrorMap.UnsubscribeOutcome(item.ResultCode);
    }
}