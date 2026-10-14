using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;
using SdkClient = MQTTnet;
using SdkLogger = MQTTnet.Diagnostics.Logger;

namespace Junevy.Communication.Mqtt.Sdk;

/// <summary>
/// 把 SDK 的日志桥接到同一个 <see cref="ILoggerFactory"/>，类别为 <c>Junevy.Communication.Mqtt.Sdk</c>（P6-C10）。
/// **只桥接 Warning 及以上**：无法逐条确认 SDK 的每一条日志都不含凭据与报文内容，
/// 因此按 P6-C10 的保守做法处理，并已记入计划的 18.2 节。
/// </summary>
internal sealed class MqttSdkLogBridge : SdkLogger.IMqttNetLogger
{
    private readonly ILogger logger;

    public MqttSdkLogBridge(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        logger = loggerFactory.CreateLogger("Junevy.Communication.Mqtt.Sdk");
    }

    /// <summary>记录器是否接收 Warning 及以上的日志（低于 Warning 的本来就不桥接）。</summary>
    public bool IsEnabled => logger.IsEnabled(LogLevel.Warning);

    /// <summary>转发一条 SDK 日志；低于 Warning 的直接丢弃。</summary>
    /// <param name="level">SDK 的日志级别。</param>
    /// <param name="source">SDK 的来源名。</param>
    /// <param name="message">SDK 的消息模板。</param>
    /// <param name="args">模板参数。</param>
    /// <param name="exception">关联的异常。</param>
    public void Publish(SdkLogger.MqttNetLogLevel level, string source, string message, object[] args, Exception? exception)
    {
        // 低于 Warning 的一律丢弃，不看记录器是不是开着：P6-C10 要求桥接前确认 SDK 的日志不含凭据与正文，
        // 而这一点无法逐条确认，因此宁可不桥接，也不冒泄漏的风险。
        if (level < SdkLogger.MqttNetLogLevel.Warning)
        {
            return;
        }

        LogLevel mapped = level == SdkLogger.MqttNetLogLevel.Warning ? LogLevel.Warning : LogLevel.Error;
        if (logger.IsEnabled(mapped))
        {
            logger.Log(mapped, exception, "[{SdkSource}] {SdkMessage}", source, Format(message, args));
        }
    }

    private static string Format(string message, object[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return message;
        }

        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, message, args);
        }
        catch (FormatException)
        {
            // 模板与参数不匹配时原样输出，不能让日志本身把 SDK 拖垮。
            return message;
        }
    }
}