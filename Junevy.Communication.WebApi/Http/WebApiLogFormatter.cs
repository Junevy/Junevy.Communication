using System.Text;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 每次尝试的 Debug 日志（内部）：方法、脱敏后的 URI、状态码、耗时；<c>LogBodies</c> 为 true 时另记请求与响应正文。
/// 输入是已经脱敏的事件参数，因此日志与事件看到的是同一份数据；只在 Debug 级别启用时才生成文本。
/// </summary>
internal sealed class WebApiLogFormatter
{
    private readonly EndpointSettings settings;
    private readonly ILogger logger;

    /// <summary>创建日志格式化器。</summary>
    /// <param name="settings">端点配置快照。</param>
    /// <param name="logger">日志记录器。</param>
    public WebApiLogFormatter(EndpointSettings settings, ILogger logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>记录即将发出的请求。</summary>
    /// <param name="args">请求事件参数（URI 与头已脱敏）。</param>
    public void LogRequest(WebApiSendingEventArgs args)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        logger.LogDebug("[{Endpoint}#{CallId}] attempt {Attempt}: {Method} {Uri}", args.EndpointName, args.CallId, args.Attempt, args.Method, args.RequestUri);
        if (settings.LogBodies && !args.Body.IsEmpty)
            logger.LogDebug("[{Endpoint}#{CallId}] attempt {Attempt}: request body: {Body}", args.EndpointName, args.CallId, args.Attempt, FormatBody(args.Body));
    }

    /// <summary>记录一次尝试的结果。</summary>
    /// <param name="args">响应事件参数（URI 已脱敏）。</param>
    public void LogResponse(WebApiResponseEventArgs args)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        string status = args.StatusCode?.ToString() ?? "no response";
        logger.LogDebug("[{Endpoint}#{CallId}] attempt {Attempt}: {Method} {Uri} -> {Status} ({Kind}) in {Elapsed} ms",
                        args.EndpointName, args.CallId, args.Attempt, args.Method, args.RequestUri, status, args.ErrorKind, (long)args.Elapsed.TotalMilliseconds);
        if (settings.LogBodies && !args.Body.IsEmpty)
            logger.LogDebug("[{Endpoint}#{CallId}] attempt {Attempt}: response body: {Body}", args.EndpointName, args.CallId, args.Attempt, FormatBody(args.Body));
    }

    // 正文按 UTF-8 解码；超过 MaxLoggedBodyLength 个字符的部分截断，并注明原长度。
    private string FormatBody(ReadOnlyMemory<byte> body)
    {
        string text = Encoding.UTF8.GetString(body.ToArray());
        return text.Length <= settings.MaxLoggedBodyLength
            ? text
            : $"{text.Substring(0, settings.MaxLoggedBodyLength)}... (truncated, {text.Length} characters in total)";
    }
}
