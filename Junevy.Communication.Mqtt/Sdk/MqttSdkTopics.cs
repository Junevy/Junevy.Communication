using SdkProtocol = MQTTnet.Protocol;
using SdkExceptions = MQTTnet.Exceptions;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// 主题与过滤器的合法性交给 SDK 的校验器，本库不另写一份（P61-D6）。这里只做一层包装：
/// 把 SDK 的异常翻译成"是否合法 + 原因"，让 <c>Sdk/</c> 之外的代码碰不到 SDK 类型（P6-C4、P6-C5）。
/// </summary>
internal static class MqttSdkTopics
{
    /// <summary>校验发布主题：不能为空，也不能含 <c>+</c> 或 <c>#</c>。</summary>
    /// <param name="topic">待校验的主题。</param>
    /// <param name="error">不合法时为 SDK 给出的原因，合法时为 null。</param>
    /// <returns>主题合法时为 true。</returns>
    public static bool TryValidatePublishTopic(string topic, out string? error) =>
        TryValidate(() => SdkProtocol.MqttTopicValidator.ThrowIfInvalid(topic), out error);

    /// <summary>校验订阅过滤器：不能为空，<c>#</c> 只能出现在最后一段。</summary>
    /// <param name="topicFilter">待校验的过滤器。</param>
    /// <param name="error">不合法时为 SDK 给出的原因，合法时为 null。</param>
    /// <returns>过滤器合法时为 true。</returns>
    public static bool TryValidateSubscribeTopic(string topicFilter, out string? error) =>
        TryValidate(() => SdkProtocol.MqttTopicValidator.ThrowIfInvalidSubscribe(topicFilter), out error);

    private static bool TryValidate(Action validate, out string? error)
    {
        try
        {
            validate();
            error = null;
            return true;
        }
        catch (SdkExceptions.MqttProtocolViolationException exception)
        {
            error = exception.Message;
            return false;
        }
    }
}