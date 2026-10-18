namespace Junevy.Communication.Mqtt;

/// <summary>
/// 收到一条消息的参数（P61-D8）。<see cref="Message"/> 的正文已经复制，与 SDK 的缓冲区无关。
/// </summary>
public sealed class MqttMessageReceivedEventArgs : EventArgs
{
    /// <summary>构造收消息的参数。</summary>
    /// <param name="message">收到的消息；不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    public MqttMessageReceivedEventArgs(MqttMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
    }

    /// <summary>收到的消息。</summary>
    public MqttMessage Message { get; }
}