using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 心跳配置的复制与校验，以及内置负载探测的创建。字节流与数据报客户端、TCP 服务端共用（P25 Task 9，补审第 4 节）。
/// 异常的参数名与消息由调用方指定，保持各自原有的文本。
/// </summary>
internal static class HeartbeatSettings
{
    /// <summary>
    /// 复制心跳配置；启用时校验间隔、超时与失败阈值（必须为正）。
    /// </summary>
    /// <param name="source">调用方的心跳配置；不能为 null。</param>
    /// <returns>副本；之后修改调用方的对象不影响它。</returns>
    /// <exception cref="ArgumentOutOfRangeException">启用心跳且某个数值非法。</exception>
    public static HeartbeatOptions Copy(HeartbeatOptions source)
    {
        var copy = new HeartbeatOptions
        {
            Enabled = source.Enabled,
            Interval = source.Interval,
            Timeout = source.Timeout,
            MaxFailures = source.MaxFailures,
            OnlyWhenIdle = source.OnlyWhenIdle,
            Payload = source.Payload,
            ExpectedReply = source.ExpectedReply,
        };

        if (copy.Enabled)
        {
            if (copy.Interval <= 0)
                throw new ArgumentOutOfRangeException("Heartbeat.Interval", copy.Interval, "The heartbeat interval must be positive.");
            if (copy.Timeout <= 0)
                throw new ArgumentOutOfRangeException("Heartbeat.Timeout", copy.Timeout, "The heartbeat timeout must be positive.");
            if (copy.MaxFailures < 1)
                throw new ArgumentOutOfRangeException("Heartbeat.MaxFailures", copy.MaxFailures, "The heartbeat failure threshold must be at least 1.");
        }

        return copy;
    }

    /// <summary>
    /// 由心跳负载构造内置探测（客户端在既没有代码级探测、也没有代码级工厂时使用，D14）。
    /// </summary>
    /// <param name="heartbeat">已复制的心跳配置（启用状态）。</param>
    /// <param name="channel">内置探测执行请求的字节通道。</param>
    /// <param name="missingPayloadParamName">负载缺失时异常的参数名；null 表示异常不带参数名（数据报客户端的既有行为）。</param>
    /// <returns>内置负载探测。</returns>
    /// <exception cref="ArgumentException">负载缺失，或负载、期望应答不是合法的字节序列。</exception>
    public static IHealthProbe CreatePayloadProbe(HeartbeatOptions heartbeat, IByteChannel channel, string? missingPayloadParamName)
    {
        if (string.IsNullOrEmpty(heartbeat.Payload))
            throw new ArgumentException("Heartbeat.Payload is required when neither IHealthProbe nor HealthProbeFactory is supplied.", missingPayloadParamName);

        byte[] payload = ParseBytes(heartbeat.Payload!, "Heartbeat.Payload", "settings");
        byte[]? expectedReply = string.IsNullOrEmpty(heartbeat.ExpectedReply)
            ? null
            : ParseBytes(heartbeat.ExpectedReply!, "Heartbeat.ExpectedReply", "settings");

        return new PayloadHeartbeatProbe(channel, payload, expectedReply, heartbeat.Timeout);
    }

    /// <summary>
    /// 解析心跳字节序列（文本，或以 <c>hex:</c> 开头的十六进制）。
    /// </summary>
    /// <param name="text">待解析的文本。</param>
    /// <param name="propertyName">配置属性名，用于错误消息。</param>
    /// <param name="paramName">异常的参数名（字节流与数据报客户端为 <c>settings</c>，TCP 服务端为 <c>Heartbeat</c>）。</param>
    /// <returns>解析后的字节。</returns>
    /// <exception cref="ArgumentException">文本不是合法的字节序列。</exception>
    public static byte[] ParseBytes(string text, string propertyName, string paramName)
    {
        try
        {
            return ByteSequenceParser.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"{propertyName} is not a valid byte sequence: {ex.Message}", paramName, ex);
        }
    }
}
