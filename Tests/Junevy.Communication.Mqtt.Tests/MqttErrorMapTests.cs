using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Sdk;
using SdkClient = MQTTnet;
using SdkExceptions = MQTTnet.Exceptions;
using SdkPackets = MQTTnet.Packets;

namespace Junevy.Communication.Mqtt.Tests;

/// <summary>
/// 计划第 15 节错误映射表的逐行覆盖：每一行一条用例。映射只有 <c>MqttErrorMap</c> 一处，
/// 因此这些用例同时钉住了 <c>CommErrorKind</c> 与 <c>ProtocolErrorCode</c> 的取值。
/// </summary>
public sealed class MqttErrorMapTests
{
    // ——— 取消与超时 ———

    [Fact]
    public void Cancellation_MapsToCancelled()
    {
        AssertOutcome(MqttErrorMap.Runtime(new OperationCanceledException()), CommErrorKind.Cancelled, null);
    }

    [Fact]
    public void SdkTimeout_MapsToTimeout()
    {
        AssertOutcome(
            MqttErrorMap.Runtime(new SdkExceptions.MqttCommunicationTimedOutException()),
            CommErrorKind.Timeout,
            null);
    }

    // ——— CONNACK ———

    [Theory]
    [InlineData(SdkClient.MqttClientConnectResultCode.BadUserNameOrPassword, 134)]
    [InlineData(SdkClient.MqttClientConnectResultCode.NotAuthorized, 135)]
    [InlineData(SdkClient.MqttClientConnectResultCode.Banned, 138)]
    [InlineData(SdkClient.MqttClientConnectResultCode.BadAuthenticationMethod, 140)]
    public void ConnectAuthenticationCodes_MapToAuthenticationFailed(SdkClient.MqttClientConnectResultCode code, int expected)
    {
        AssertOutcome(MqttErrorMap.ConnectOutcome(code), CommErrorKind.AuthenticationFailed, expected);
    }

    [Theory]
    [InlineData(SdkClient.MqttClientConnectResultCode.ServerBusy, 137)]
    [InlineData(SdkClient.MqttClientConnectResultCode.QuotaExceeded, 151)]
    [InlineData(SdkClient.MqttClientConnectResultCode.ConnectionRateExceeded, 159)]
    public void ConnectResourceCodes_MapToResourceExhausted(SdkClient.MqttClientConnectResultCode code, int expected)
    {
        AssertOutcome(MqttErrorMap.ConnectOutcome(code), CommErrorKind.ResourceExhausted, expected);
    }

    [Fact]
    public void UnsupportedProtocolVersion_MapsToNotSupported()
    {
        AssertOutcome(
            MqttErrorMap.ConnectOutcome(SdkClient.MqttClientConnectResultCode.UnsupportedProtocolVersion),
            CommErrorKind.NotSupported,
            132);
    }

    [Theory]
    [InlineData(SdkClient.MqttClientConnectResultCode.UnspecifiedError, 128)]
    [InlineData(SdkClient.MqttClientConnectResultCode.MalformedPacket, 129)]
    [InlineData(SdkClient.MqttClientConnectResultCode.ProtocolError, 130)]
    [InlineData(SdkClient.MqttClientConnectResultCode.ImplementationSpecificError, 131)]
    [InlineData(SdkClient.MqttClientConnectResultCode.ClientIdentifierNotValid, 133)]
    [InlineData(SdkClient.MqttClientConnectResultCode.ServerUnavailable, 136)]
    [InlineData(SdkClient.MqttClientConnectResultCode.TopicNameInvalid, 144)]
    [InlineData(SdkClient.MqttClientConnectResultCode.PacketTooLarge, 149)]
    [InlineData(SdkClient.MqttClientConnectResultCode.QoSNotSupported, 155)]
    [InlineData(SdkClient.MqttClientConnectResultCode.ServerMoved, 157)]
    public void OtherConnectCodes_MapToRemoteError(SdkClient.MqttClientConnectResultCode code, int expected)
    {
        AssertOutcome(MqttErrorMap.ConnectOutcome(code), CommErrorKind.RemoteError, expected);
    }

    [Fact]
    public void ConnectAccepted_MapsToSuccess()
    {
        Assert.True(MqttErrorMap.ConnectOutcome(SdkClient.MqttClientConnectResultCode.Success).IsSuccess);
    }

    // ——— PUBACK / PUBREC ———

    [Fact]
    public void PublishQuotaExceeded_MapsToResourceExhausted()
    {
        AssertOutcome(
            MqttErrorMap.PublishOutcome(SdkClient.MqttClientPublishReasonCode.QuotaExceeded),
            CommErrorKind.ResourceExhausted,
            151);
    }

    [Theory]
    [InlineData(SdkClient.MqttClientPublishReasonCode.UnspecifiedError, 128)]
    [InlineData(SdkClient.MqttClientPublishReasonCode.ImplementationSpecificError, 131)]
    [InlineData(SdkClient.MqttClientPublishReasonCode.NotAuthorized, 135)]
    [InlineData(SdkClient.MqttClientPublishReasonCode.TopicNameInvalid, 144)]
    [InlineData(SdkClient.MqttClientPublishReasonCode.PacketIdentifierInUse, 145)]
    [InlineData(SdkClient.MqttClientPublishReasonCode.PayloadFormatInvalid, 153)]
    public void OtherPublishCodes_MapToRemoteError(SdkClient.MqttClientPublishReasonCode code, int expected)
    {
        AssertOutcome(MqttErrorMap.PublishOutcome(code), CommErrorKind.RemoteError, expected);
    }

    [Theory]
    [InlineData(SdkClient.MqttClientPublishReasonCode.Success, 0)]
    [InlineData(SdkClient.MqttClientPublishReasonCode.NoMatchingSubscribers, 16)]
    public void PublishCodesBelow0x80_MapToSuccess(SdkClient.MqttClientPublishReasonCode code, int expected)
    {
        Assert.True(MqttErrorMap.PublishOutcome(code).IsSuccess);
        Assert.Equal(expected, (int)code);
    }

    // ——— SUBACK / UNSUBACK ———

    [Theory]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.UnspecifiedError, 128)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.ImplementationSpecificError, 131)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.NotAuthorized, 135)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.TopicFilterInvalid, 143)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.PacketIdentifierInUse, 145)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.QuotaExceeded, 151)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.SharedSubscriptionsNotSupported, 158)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.SubscriptionIdentifiersNotSupported, 161)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.WildcardSubscriptionsNotSupported, 162)]
    public void SubscribeFailures_MapToRemoteError(SdkClient.MqttClientSubscribeResultCode code, int expected)
    {
        AssertOutcome(MqttErrorMap.SubscribeOutcome(code, out _), CommErrorKind.RemoteError, expected);
    }

    [Theory]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.GrantedQoS0, MqttQos.AtMostOnce)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.GrantedQoS1, MqttQos.AtLeastOnce)]
    [InlineData(SdkClient.MqttClientSubscribeResultCode.GrantedQoS2, MqttQos.ExactlyOnce)]
    public void GrantedSubscriptions_MapToSuccessWithTheGrantedQos(SdkClient.MqttClientSubscribeResultCode code, MqttQos expected)
    {
        CommResult outcome = MqttErrorMap.SubscribeOutcome(code, out MqttQos granted);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(expected, granted);
    }

    [Theory]
    [InlineData(SdkClient.MqttClientUnsubscribeResultCode.UnspecifiedError, 128)]
    [InlineData(SdkClient.MqttClientUnsubscribeResultCode.ImplementationSpecificError, 131)]
    [InlineData(SdkClient.MqttClientUnsubscribeResultCode.NotAuthorized, 135)]
    [InlineData(SdkClient.MqttClientUnsubscribeResultCode.TopicFilterInvalid, 143)]
    [InlineData(SdkClient.MqttClientUnsubscribeResultCode.PacketIdentifierInUse, 145)]
    public void UnsubscribeFailures_MapToRemoteError(SdkClient.MqttClientUnsubscribeResultCode code, int expected)
    {
        AssertOutcome(MqttErrorMap.UnsubscribeOutcome(code), CommErrorKind.RemoteError, expected);
    }

    [Theory]
    [InlineData(SdkClient.MqttClientUnsubscribeResultCode.Success)]
    [InlineData(SdkClient.MqttClientUnsubscribeResultCode.NoSubscriptionExisted)]
    public void UnsubscribeCodesBelow0x80_MapToSuccess(SdkClient.MqttClientUnsubscribeResultCode code)
    {
        Assert.True(MqttErrorMap.UnsubscribeOutcome(code).IsSuccess);
    }

    // ——— 运行期异常 ———

    [Fact]
    public void TlsFailure_MapsToAuthenticationFailed()
    {
        AssertOutcome(
            MqttErrorMap.Runtime(new AuthenticationException("untrusted root")),
            CommErrorKind.AuthenticationFailed,
            null);
    }

    [Theory]
    [InlineData(typeof(SdkExceptions.MqttClientDisconnectedException))]
    [InlineData(typeof(SdkExceptions.MqttClientUnexpectedDisconnectReceivedException))]
    [InlineData(typeof(SdkExceptions.MqttProxyException))]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    public void CommunicationFailures_MapToConnectionClosed(Type exceptionType)
    {
        AssertOutcome(MqttErrorMap.Runtime(SdkException(exceptionType)), CommErrorKind.ConnectionClosed, null);
    }

    [Fact]
    public void NotConnectedFromTheSdk_MapsToNotConnected()
    {
        AssertOutcome(
            MqttErrorMap.Runtime(new SdkExceptions.MqttClientNotConnectedException()),
            CommErrorKind.NotConnected,
            null);
    }

    [Theory]
    [InlineData(typeof(SdkExceptions.MqttConfigurationException))]
    [InlineData(typeof(SdkExceptions.MqttProtocolViolationException))]
    [InlineData(typeof(InvalidOperationException))]
    public void OtherExceptions_MapToUnspecified(Type exceptionType)
    {
        AssertOutcome(MqttErrorMap.Runtime(SdkException(exceptionType)), CommErrorKind.Unspecified, null);
    }

    [Fact]
    public void RuntimeKeepsTheExceptionForDiagnostics()
    {
        var exception = new SdkExceptions.MqttCommunicationException("boom");

        Assert.Same(exception, MqttErrorMap.Runtime(exception).Exception);
    }

    [Fact]
    public void RuntimeWithNull_Throws()
    {
        Assert.Throws<ArgumentNullException>("exception", () => MqttErrorMap.Runtime(null!));
    }

    // ——— 本地请求非法 ———

    [Fact]
    public void InvalidRequest_CarriesNoProtocolCode()
    {
        CommResult outcome = MqttErrorMap.InvalidRequest("The topic is not valid.");

        AssertOutcome(outcome, CommErrorKind.InvalidRequest, null);
        Assert.Equal("The topic is not valid.", outcome.ErrorMessage);
    }

    private static void AssertOutcome(CommResult outcome, CommErrorKind kind, long? protocolErrorCode)
    {
        Assert.False(outcome.IsSuccess);
        Assert.Equal(kind, outcome.ErrorKind);
        Assert.Equal(protocolErrorCode, outcome.ProtocolErrorCode);
        Assert.False(string.IsNullOrEmpty(outcome.ErrorMessage));
    }

    // SDK 各异常的构造函数签名并不统一，这里按类型给出可用的那一个，避免用例依赖反射。
    private static Exception SdkException(Type exceptionType)
    {
        var closed = new IOException("The connection was closed.");

        return exceptionType == typeof(SdkExceptions.MqttClientDisconnectedException) ? new SdkExceptions.MqttClientDisconnectedException(closed)
            : exceptionType == typeof(SdkExceptions.MqttClientUnexpectedDisconnectReceivedException)
                ? new SdkExceptions.MqttClientUnexpectedDisconnectReceivedException(new SdkPackets.MqttDisconnectPacket(), closed)
            : exceptionType == typeof(SdkExceptions.MqttProxyException)
                ? new SdkExceptions.MqttProxyException(SdkExceptions.MqttProxyErrorCode.ProxyUnreachable, "boom", "127.0.0.1:1080", null)
            : exceptionType == typeof(SdkExceptions.MqttConfigurationException) ? new SdkExceptions.MqttConfigurationException("boom")
            : exceptionType == typeof(SdkExceptions.MqttProtocolViolationException) ? new SdkExceptions.MqttProtocolViolationException("boom")
            : exceptionType == typeof(SocketException) ? new SocketException(10054)
            : exceptionType == typeof(IOException) ? closed
            : exceptionType == typeof(InvalidOperationException) ? new InvalidOperationException("boom")
            : throw new NotSupportedException($"No sample instance for {exceptionType.FullName}.");
    }
}