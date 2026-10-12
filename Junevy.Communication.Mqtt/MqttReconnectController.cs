using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Sdk;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 重连策略（P61-D9、P6-C11）：决定"什么时候重连、重连成功后做什么"。循环本身的驱动在
/// <see cref="MqttReconnectLoop"/>，这里是 MQTT 特有的那一半：
/// <list type="bullet">
/// <item>触发：SDK 报告"原本已连接、并非我方主动断开"的断线 → 状态转 <see cref="MqttConnectionState.Reconnecting"/>（<c>Reason</c> 取 SDK 给出的原因），
/// 然后在后台任务里启动循环；<b>不在 SDK 的事件回调里重连</b>（MQTTnet 明确警告这样会死锁）。</item>
/// <item>成功：Broker 回的 Session Present 为 false 时，按订阅表逐个重新订阅，然后才转
/// <see cref="MqttConnectionState.Connected"/>（P61-D5）；为 true 时不重新订阅，避免 Broker 重发保留消息。</item>
/// <item>关闭：<c>AutoReconnect</c> 为 false 时断线直接转 <see cref="MqttConnectionState.Disconnected"/>。</item>
/// </list>
/// </summary>
internal sealed class MqttReconnectController
{
    private readonly MqttSdkClient sdk;
    private readonly MqttConnectionStateMachine state;
    private readonly MqttMessaging messaging;
    private readonly MqttLifecycleGate lifecycle;
    private readonly bool enabled;
    private readonly MqttReconnectLoop loop;
    private readonly ILogger logger;

    /// <summary>创建重连控制器。</summary>
    /// <param name="sdk">SDK 客户端的包装。</param>
    /// <param name="state">连接状态机。</param>
    /// <param name="messaging">消息操作（恢复订阅走它的路径）。</param>
    /// <param name="lifecycle">生命周期锁；每一次重连尝试都要经过它。</param>
    /// <param name="enabled">对应 <c>MqttClientOptions.AutoReconnect</c>。</param>
    /// <param name="reconnectInterval">第一次重连前的等待（毫秒）。</param>
    /// <param name="maxReconnectInterval">退避上限（毫秒）。</param>
    /// <param name="logger">日志。</param>
    public MqttReconnectController(
        MqttSdkClient sdk,
        MqttConnectionStateMachine state,
        MqttMessaging messaging,
        MqttLifecycleGate lifecycle,
        bool enabled,
        int reconnectInterval,
        int maxReconnectInterval,
        ILogger logger)
    {
        this.sdk = sdk;
        this.state = state;
        this.messaging = messaging;
        this.lifecycle = lifecycle;
        this.enabled = enabled;
        this.logger = logger;

        loop = new MqttReconnectLoop(
            new ExponentialBackoff(reconnectInterval, maxReconnectInterval),
            TryReconnectAsync,
            OnReconnectFailed,
            logger);
    }

    /// <summary>SDK 报告断线时调用；只有"原本已连接、而且不是我们自己在断开"才反应。</summary>
    /// <param name="clientWasConnected">断线之前是否已经连上过。</param>
    /// <param name="reason">SDK 给出的断开原因。</param>
    public void OnUnexpectedDisconnect(bool clientWasConnected, string? reason)
    {
        // 首次连接失败不重连（P6-C11）。只有状态仍是 Connected 才反应，而且判断与修改一步完成：
        // 应用正在断开时状态已经是 Disconnected，这里不会把它改回去，也不会启动重连。
        if (!clientWasConnected)
        {
            return;
        }

        if (!enabled)
        {
            state.TryMoveTo(MqttConnectionState.Connected, MqttConnectionState.Disconnected, reason);
            return;
        }

        if (state.TryMoveTo(MqttConnectionState.Connected, MqttConnectionState.Reconnecting, reason))
        {
            loop.TryStart();
        }
    }

    /// <summary>结束当前这一轮重连（如果有）并等待它退出；之后的断线仍会重连。</summary>
    /// <returns>这一轮已退出。</returns>
    public Task StopAsync() => loop.StopAsync();

    /// <summary>永久停止重连并等待它退出。可重复调用。</summary>
    /// <returns>已停止。</returns>
    public ValueTask DisposeAsync() => loop.DisposeAsync();

    private void OnReconnectFailed(int attempt, CommResult outcome) =>
        logger.LogWarning(
            "Reconnect attempt {Attempt} failed: {ErrorKind} - {ErrorMessage}",
            attempt,
            outcome.ErrorKind,
            outcome.ErrorMessage);

    private async Task<CommResult> TryReconnectAsync(CancellationToken cancellationToken)
    {
        // 每次重连尝试都走同一把生命周期锁（P61-D10）。
        await using IAsyncDisposable lease = await lifecycle.EnterAsync(cancellationToken).ConfigureAwait(false);

        // 应用的断开与释放都在锁内把状态改成 Disconnected：拿到锁时已经不是 Reconnecting，这一轮就此结束。
        if (state.Current != MqttConnectionState.Reconnecting)
        {
            throw new OperationCanceledException("The application disconnected the client.");
        }

        (CommResult outcome, bool sessionPresent) = await sdk.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!outcome.IsSuccess)
        {
            return outcome;
        }

        // Broker 保留了会话就不重新订阅（P61-D9）。恢复时遇到链路问题，这一轮算失败：断开后由循环再试，订阅表不变。
        if (!sessionPresent)
        {
            CommResult restored = await messaging.RestoreSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
            if (!restored.IsSuccess)
            {
                await sdk.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                return restored;
            }
        }

        // 恢复期间又断线时，SDK 的断线事件因为状态不是 Connected 而被忽略，因此在这里补查一次。
        if (!sdk.IsConnected)
        {
            return CommResult.Fail("The connection was lost while the subscriptions were being restored.", CommErrorKind.ConnectionClosed);
        }

        // 恢复完订阅才报 Connected：宿主看到它时订阅已经可用（P61-D5）。
        state.MoveTo(MqttConnectionState.Connected, null);
        return outcome;
    }
}