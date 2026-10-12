using System.Net.Sockets;
using System.Security.Authentication;
using Junevy.Communication.Core.Results;
using SdkClient = MQTTnet;
using SdkExceptions = MQTTnet.Exceptions;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// MQTT 的失败到 <see cref="CommResult"/> 的映射，是全包唯一一处由 SDK 的异常或原因码推出
/// <see cref="CommErrorKind"/> 的地方（P6-C7、Q38）。升级 SDK 时只有这一个文件需要改。
/// 逐行的映射表见计划第 15 节，每一行都有对应的测试。
/// </summary>
internal static class MqttErrorMap
{
    /// <summary>
    /// MQTT 5 的原因码以 0x80 为界：小于它表示成功（例如 PUBACK 的 0x10"No matching subscribers"），大于等于它表示失败。
    /// </summary>
    private const int FailureThreshold = 0x80;

    /// <summary>CONNACK 的结果码 → 结果。3.1.1 的返回码由 SDK 换算到这里的枚举。</summary>
    /// <param name="code">SDK 的连接结果码。</param>
    /// <returns>Broker 接受连接时成功，否则按映射表返回分类与原因码。</returns>
    public static CommResult ConnectOutcome(SdkClient.MqttClientConnectResultCode code) =>
        (int)code < FailureThreshold
            ? CommResult.Success()
            : Fail(ConnectKind(code), (int)code, $"The broker rejected the connection: {code}.", null);

    /// <summary>PUBACK / PUBREC 的原因码 → 结果。</summary>
    /// <param name="code">SDK 的发布原因码。</param>
    /// <returns>原因码小于 0x80 时成功，否则按映射表返回分类与原因码。</returns>
    public static CommResult PublishOutcome(SdkClient.MqttClientPublishReasonCode code) =>
        (int)code < FailureThreshold
            ? CommResult.Success()
            : Fail(
                code == SdkClient.MqttClientPublishReasonCode.QuotaExceeded ? CommErrorKind.ResourceExhausted : CommErrorKind.RemoteError,
                (int)code,
                $"The broker refused the published message: {code}.",
                null);

    /// <summary>SUBACK 的结果码 → 结果。</summary>
    /// <param name="code">SDK 的订阅结果码。</param>
    /// <param name="grantedQos">Broker 授予的 QoS；只在结果成功时有意义。</param>
    /// <returns>Broker 授予 QoS 时成功，否则按映射表返回分类与原因码。</returns>
    public static CommResult SubscribeOutcome(SdkClient.MqttClientSubscribeResultCode code, out MqttQos grantedQos)
    {
        grantedQos = GrantedQos(code);
        return (int)code < FailureThreshold
            ? CommResult.Success()
            : Fail(CommErrorKind.RemoteError, (int)code, $"The broker rejected the subscription: {code}.", null);
    }

    /// <summary>UNSUBACK 的结果码 → 结果。</summary>
    /// <param name="code">SDK 的退订结果码。</param>
    /// <returns>退订被接受时成功，否则按映射表返回分类与原因码。</returns>
    public static CommResult UnsubscribeOutcome(SdkClient.MqttClientUnsubscribeResultCode code) =>
        (int)code < FailureThreshold
            ? CommResult.Success()
            : Fail(CommErrorKind.RemoteError, (int)code, $"The broker rejected the unsubscription: {code}.", null);

    /// <summary>运行期异常 → 结果。SDK 抛出的异常不能逃出公开方法（P6-C8）。</summary>
    /// <param name="exception">SDK 或 BCL 抛出的异常。</param>
    /// <returns>带分类的结果；异常只作诊断。</returns>
    public static CommResult Runtime(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 顺序要紧：超时与"未连接"都是 MqttCommunicationException 的子类，必须排在它前面。
        return exception switch
        {
            OperationCanceledException => Fail(CommErrorKind.Cancelled, null, "The operation was cancelled.", exception),
            AuthenticationException => Fail(CommErrorKind.AuthenticationFailed, null, $"TLS authentication failed: {exception.Message}", exception),
            SdkExceptions.MqttCommunicationTimedOutException => Fail(CommErrorKind.Timeout, null, "The MQTT operation timed out.", exception),
            SdkExceptions.MqttClientNotConnectedException => Fail(CommErrorKind.NotConnected, null, "The MQTT client is not connected.", exception),
            SdkExceptions.MqttCommunicationException => Fail(CommErrorKind.ConnectionClosed, null, $"The MQTT connection failed: {exception.Message}", exception),
            IOException or SocketException => Fail(CommErrorKind.ConnectionClosed, null, $"The connection failed: {exception.Message}", exception),
            _ => Fail(CommErrorKind.Unspecified, null, $"The MQTT operation failed with {exception.GetType().Name}: {exception.Message}", exception),
        };
    }

    /// <summary>请求本身不合法：主题或过滤器非法、3.1.1 下带 MQTT 5 属性、QoS 不是定义值（P61-D6）。不发送。</summary>
    /// <param name="message">写明具体哪一项非法。</param>
    /// <param name="exception">SDK 校验器给出的异常；只作诊断。</param>
    /// <returns><see cref="CommErrorKind.InvalidRequest"/> 的结果。</returns>
    public static CommResult InvalidRequest(string message, Exception? exception = null) =>
        CommResult.Fail(message, CommErrorKind.InvalidRequest, exception: exception);

    private static CommErrorKind ConnectKind(SdkClient.MqttClientConnectResultCode code) => code switch
    {
        SdkClient.MqttClientConnectResultCode.BadUserNameOrPassword
            or SdkClient.MqttClientConnectResultCode.NotAuthorized
            or SdkClient.MqttClientConnectResultCode.Banned
            or SdkClient.MqttClientConnectResultCode.BadAuthenticationMethod => CommErrorKind.AuthenticationFailed,
        SdkClient.MqttClientConnectResultCode.ServerBusy
            or SdkClient.MqttClientConnectResultCode.QuotaExceeded
            or SdkClient.MqttClientConnectResultCode.ConnectionRateExceeded => CommErrorKind.ResourceExhausted,
        SdkClient.MqttClientConnectResultCode.UnsupportedProtocolVersion => CommErrorKind.NotSupported,
        _ => CommErrorKind.RemoteError,
    };

    private static MqttQos GrantedQos(SdkClient.MqttClientSubscribeResultCode code) => code switch
    {
        SdkClient.MqttClientSubscribeResultCode.GrantedQoS1 => MqttQos.AtLeastOnce,
        SdkClient.MqttClientSubscribeResultCode.GrantedQoS2 => MqttQos.ExactlyOnce,
        _ => MqttQos.AtMostOnce,
    };

    private static CommResult Fail(CommErrorKind kind, long? protocolErrorCode, string message, Exception? exception) =>
        CommResult.Fail(message, kind, protocolErrorCode, exception);
}