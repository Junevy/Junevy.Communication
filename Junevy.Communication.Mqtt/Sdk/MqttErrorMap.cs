using System.Net.Sockets;
using System.Security.Authentication;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
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

    /// <summary>SUBACK 的结果码 → 结果（成功时带上 Broker 授予的 QoS）。</summary>
    /// <param name="code">SDK 的订阅结果码。</param>
    /// <returns>Broker 授予 QoS 时成功，否则按映射表返回分类与原因码。</returns>
    public static CommResult<MqttQos> SubscribeOutcome(SdkClient.MqttClientSubscribeResultCode code) =>
        (int)code < FailureThreshold
            ? CommResult<MqttQos>.Success(GrantedQos(code))
            : CommResult<MqttQos>.Fail($"The broker rejected the subscription: {code}.", CommErrorKind.RemoteError, (int)code);

    /// <summary>UNSUBACK 的结果码 → 结果。</summary>
    /// <param name="code">SDK 的退订结果码。</param>
    /// <returns>退订被接受时成功，否则按映射表返回分类与原因码。</returns>
    public static CommResult UnsubscribeOutcome(SdkClient.MqttClientUnsubscribeResultCode code) =>
        (int)code < FailureThreshold
            ? CommResult.Success()
            : Fail(CommErrorKind.RemoteError, (int)code, $"The broker rejected the unsubscription: {code}.", null);

    /// <summary>运行期异常 → 结果。SDK 抛出的异常不能逃出公开方法（P6-C8）。</summary>
    /// <param name="exception">SDK 或 BCL 抛出的异常。</param>
    /// <param name="scope">包住这次操作的超时作用域；给了它就以"是不是我们自己超时 / 调用方取消"为准，
    /// 不再猜异常的类型——MQTTnet 在连接被中止时抛的是笼统的通信异常，类型里读不出原因。</param>
    /// <returns>带分类的结果；异常只作诊断。</returns>
    public static CommResult Runtime(Exception exception, TimeoutScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 先看作用域：调用方取消与超时由我们自己掌握，比异常类型可靠。
        if (scope is not null)
        {
            if (scope.IsUserCancelled)
            {
                return Fail(CommErrorKind.Cancelled, null, "The operation was cancelled.", exception);
            }

            if (scope.IsTimedOut)
            {
                return Fail(CommErrorKind.Timeout, null, "The MQTT operation timed out.", exception);
            }
        }

        // 顺序要紧：超时与"未连接"都是 MqttCommunicationException 的子类，必须排在它前面。
        return exception switch
        {
            OperationCanceledException => Fail(CommErrorKind.Cancelled, null, "The operation was cancelled.", exception),
            SdkExceptions.MqttCommunicationTimedOutException => Fail(CommErrorKind.Timeout, null, "The MQTT operation timed out.", exception),
            SdkExceptions.MqttClientNotConnectedException => Fail(CommErrorKind.NotConnected, null, "The MQTT client is not connected.", exception),
            _ => Classify(exception),
        };
    }

    // TLS 的认证异常会被 MQTTnet 包进 MqttCommunicationException，因此要沿内层异常链找一遍（P6-C14 / 18.2 的 F7）。
    private static CommResult Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return Fail(CommErrorKind.AuthenticationFailed, null, $"TLS authentication failed: {current.Message}", exception);
            }
        }

        return exception switch
        {
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