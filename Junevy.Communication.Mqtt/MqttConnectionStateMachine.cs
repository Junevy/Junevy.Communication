using Junevy.Communication.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// 连接状态的唯一来源（Q35）：状态只在这里变化，并按发生顺序通知 <c>StateChanged</c>。
/// 读用 <see cref="Volatile"/>，写用 <see cref="Interlocked"/>，因此任何线程都能安全地读到一致的状态快照。
/// </summary>
internal sealed class MqttConnectionStateMachine
{
    private readonly object sender;
    private readonly ILogger logger;
    private int current = (int)MqttConnectionState.Disconnected;

    public MqttConnectionStateMachine(object sender, ILogger logger)
    {
        this.sender = sender;
        this.logger = logger;
    }

    /// <summary>状态发生变化时触发；订阅者抛异常只记 Error，不影响状态，也不影响其余订阅者。</summary>
    public event EventHandler<MqttConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>当前状态。</summary>
    public MqttConnectionState Current => (MqttConnectionState)Volatile.Read(ref current);

    /// <summary>把状态改为 <paramref name="next"/> 并通知；状态没变时不通知。</summary>
    /// <param name="next">目标状态。</param>
    /// <param name="reason">变化原因；没有时为 null。</param>
    /// <returns>变化前的状态。</returns>
    public MqttConnectionState MoveTo(MqttConnectionState next, string? reason)
    {
        MqttConnectionState previous = (MqttConnectionState)Interlocked.Exchange(ref current, (int)next);
        if (previous == next)
        {
            return previous;
        }

        logger.LogInformation("MQTT state changed from {PreviousState} to {CurrentState}.", previous, next);
        EventSubscribers.InvokeEach(
            StateChanged,
            sender,
            new MqttConnectionStateChangedEventArgs(previous, next, reason),
            logger,
            nameof(MqttClient.StateChanged));

        return previous;
    }
}