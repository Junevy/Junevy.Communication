using System.Text;
using SdkClient = MQTTnet;
using SdkFormatter = MQTTnet.Formatter;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// 把本库的选项映射成 SDK 的选项。映射只在这里发生，SDK 类型不外泄（P6-C4、P6-C5）。
/// </summary>
internal static class MqttSdkOptions
{
    /// <summary>按本库选项构造 SDK 选项。</summary>
    /// <param name="options">已校验的选项；映射在此处取快照，之后本库不再读这个对象。</param>
    /// <returns>SDK 的连接选项。</returns>
    public static SdkClient.MqttClientOptions Build(MqttClientOptions options)
    {
        var builder = new SdkClient.MqttClientOptionsBuilder()
            .WithTcpServer(options.Host!, options.Port)
            .WithClientId(options.ClientId!)
            .WithProtocolVersion(ProtocolVersion(options.ProtocolVersion))
            .WithCleanSession(options.CleanSession)
            .WithTimeout(TimeSpan.FromMilliseconds(options.Timeout));

        ApplyKeepAlive(builder, options.KeepAliveSeconds);
        ApplySessionExpiry(builder, options);
        ApplyCredentials(builder, options);
        ApplyTls(builder, options);
        ApplyWill(builder, options);

        return builder.Build();
    }

    // MQTTnet 5 的 MqttClientOptions 上没有 AutoReconnect：重连由本包的后台循环负责（P61-D9），因此这里不设置。
    private static SdkFormatter.MqttProtocolVersion ProtocolVersion(MqttProtocolVersion version) => MqttSdkMessages.SdkVersion(version);

    private static void ApplyKeepAlive(SdkClient.MqttClientOptionsBuilder builder, int seconds)
    {
        if (seconds == 0)
        {
            builder.WithNoKeepAlive();
            return;
        }

        builder.WithKeepAlivePeriod(TimeSpan.FromSeconds(seconds));
    }

    private static void ApplySessionExpiry(SdkClient.MqttClientOptionsBuilder builder, MqttClientOptions options)
    {
        if (options.SessionExpiryInterval != 0)
        {
            builder.WithSessionExpiryInterval(options.SessionExpiryInterval);
        }
    }

    private static void ApplyCredentials(SdkClient.MqttClientOptionsBuilder builder, MqttClientOptions options)
    {
        if (options.UserName is not null)
        {
            builder.WithCredentials(options.UserName, options.Password ?? string.Empty);
        }
    }

    private static void ApplyTls(SdkClient.MqttClientOptionsBuilder builder, MqttClientOptions options)
    {
        if (!options.UseTls)
        {
            return;
        }

        builder.WithTlsOptions(tls =>
        {
            tls.UseTls(true).WithTargetHost(options.Host!);

            // P61-D4："接受任何证书，包括主机名不符"。SDK 自己的 AllowUntrustedCertificates 只放过证书链错误，
            // 主机名不符照样拒绝（验收时实测，见 MqttTlsTests），因此改为由校验回调一律放行。
            if (options.AllowUntrustedCertificates)
            {
                tls.WithCertificateValidationHandler(_ => true);
            }
        });
    }

    // 遗嘱放在连接选项里，因此每次连接（含重连）都会在 CONNECT 中带上（P61-D14）。
    private static void ApplyWill(SdkClient.MqttClientOptionsBuilder builder, MqttClientOptions options)
    {
        if (options.WillTopic is null)
        {
            return;
        }

        byte[] payload = Encoding.UTF8.GetBytes(options.WillPayload ?? string.Empty);
        builder
            .WithWillTopic(options.WillTopic)
            .WithWillPayload(payload)
            .WithWillQualityOfServiceLevel(MqttSdkQos.ToSdk(options.WillQos))
            .WithWillRetain(options.WillRetain);
    }
}