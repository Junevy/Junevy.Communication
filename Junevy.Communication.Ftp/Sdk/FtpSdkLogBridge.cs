using FluentFTP;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// 把 FluentFTP 的日志桥接到同一个 <see cref="ILoggerFactory"/>，类别为 <c>Junevy.Communication.Ftp.Sdk</c>（P6-C10、P63-D15）。
/// 级别对应：Verbose → Trace，Info → Debug，Warn → Warning，Error → Error。
/// 主机名、用户名、密码的脱敏开关保持 SDK 的默认值（全部脱敏，见 <see cref="FtpSdkConfigMap"/>）。
/// </summary>
internal sealed class FtpSdkLogBridge : IFtpLogger
{
    private const string MessageTemplate = "{SdkMessage}";

    private readonly ILogger logger;

    /// <summary>创建桥接器。</summary>
    /// <param name="loggerFactory">日志工厂。</param>
    public FtpSdkLogBridge(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger("Junevy.Communication.Ftp.Sdk");
    }

    /// <summary>转发一条 SDK 日志。</summary>
    /// <param name="entry">SDK 的日志条目。</param>
    public void Log(FtpLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        LogLevel level = entry.Severity switch
        {
            FtpTraceLevel.Verbose => LogLevel.Trace,
            FtpTraceLevel.Info => LogLevel.Debug,
            FtpTraceLevel.Warn => LogLevel.Warning,
            _ => LogLevel.Error,
        };

        if (logger.IsEnabled(level))
        {
            logger.Log(level, entry.Exception, MessageTemplate, entry.Message);
        }
    }
}
