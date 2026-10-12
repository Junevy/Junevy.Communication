using Junevy.Communication.Mqtt.Sdk;

namespace Junevy.Communication.Mqtt;

/// <summary>
/// <see cref="MqttClientOptions"/> 的校验。全部集中在 <c>MqttClient.Create</c> 时执行一次，之后取快照，
/// 非法时抛 <see cref="ArgumentException"/> 族，消息写明属性名与允许范围。遗嘱主题的合法性交给 SDK 的校验器（P61-D6）。
/// </summary>
internal static class MqttOptionsValidator
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const int MaxKeepAliveSeconds = 65535;

    /// <summary>校验选项。</summary>
    /// <param name="options">待校验的选项；不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个属性取值非法。</exception>
    public static void Validate(MqttClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RequireText(options.Host, "Host");
        RequireText(options.ClientId, "ClientId");
        RequireRange(options.Port, MinPort, MaxPort, "Port");
        RequireRange(options.KeepAliveSeconds, 0, MaxKeepAliveSeconds, "KeepAliveSeconds");
        RequirePositive(options.Timeout, "Timeout");
        RequirePositive(options.ReconnectInterval, "ReconnectInterval");
        RequireDefined(options.ProtocolVersion, "ProtocolVersion");
        RequireDefined(options.WillQos, "WillQos");
        RequireWillDefaultsWithoutTopic(options);
        RequireSessionExpiryWithV500(options);
        RequireUserNameWithPassword(options);
        RequireMaxNotBelow(options);
        RequireValidWillTopic(options);
    }

    private static void RequireText(string? value, string property)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"MqttClientOptions.{property} must not be null or empty.", PropertyPath(property));
        }
    }

    // ParamName 统一写成 "options.X"：调用方一眼能看出是哪个参数上的哪个属性非法。
    private static string PropertyPath(string property) => "options." + property;

    private static void RequireRange(int value, int minimum, int maximum, string property)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                PropertyPath(property),
                value,
                $"MqttClientOptions.{property} must be between {minimum} and {maximum}.");
        }
    }

    private static void RequirePositive(int value, string property)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                PropertyPath(property),
                value,
                $"MqttClientOptions.{property} must be greater than 0.");
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, string property)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"MqttClientOptions.{property} has a value that is not defined.", PropertyPath(property));
        }
    }

    // 没给遗嘱主题时，遗嘱正文、保留标志与 QoS 都必须保持默认值：设了却不生效，多半是配置写错了（P61-D14）。
    private static void RequireWillDefaultsWithoutTopic(MqttClientOptions options)
    {
        if (options.WillTopic is not null)
        {
            return;
        }

        if (options.WillPayload is null && !options.WillRetain && options.WillQos == MqttQos.AtMostOnce)
        {
            return;
        }

        throw new ArgumentException(
            "MqttClientOptions.WillPayload, WillRetain and WillQos only take effect together with WillTopic; WillTopic is null.",
            PropertyPath("WillTopic"));
    }

    private static void RequireSessionExpiryWithV500(MqttClientOptions options)
    {
        if (options.SessionExpiryInterval != 0 && options.ProtocolVersion != MqttProtocolVersion.V500)
        {
            throw new ArgumentException(
                "MqttClientOptions.SessionExpiryInterval is an MQTT 5 property and can only be non-zero with MqttClientOptions.ProtocolVersion = V500.",
                PropertyPath("SessionExpiryInterval"));
        }
    }

    // 3.1.1 的规定：只有密码没有用户名是不允许的。
    private static void RequireUserNameWithPassword(MqttClientOptions options)
    {
        if (options.Password is not null && options.UserName is null)
        {
            throw new ArgumentException(
                "MqttClientOptions.UserName is required when MqttClientOptions.Password is set.",
                PropertyPath("UserName"));
        }
    }

    private static void RequireMaxNotBelow(MqttClientOptions options)
    {
        if (options.MaxReconnectInterval < options.ReconnectInterval)
        {
            throw new ArgumentException(
                $"MqttClientOptions.MaxReconnectInterval ({options.MaxReconnectInterval}) must not be smaller than MqttClientOptions.ReconnectInterval ({options.ReconnectInterval}).",
                PropertyPath("MaxReconnectInterval"));
        }
    }

    // 合法性判定交给 SDK 的校验器，本库不另写一份主题规则（P61-D6）。
    private static void RequireValidWillTopic(MqttClientOptions options)
    {
        if (options.WillTopic is null)
        {
            return;
        }

        if (!MqttSdkTopics.TryValidatePublishTopic(options.WillTopic, out string? error))
        {
            throw new ArgumentException(
                $"MqttClientOptions.WillTopic is not a valid publish topic: {error}",
                PropertyPath("WillTopic"));
        }
    }
}