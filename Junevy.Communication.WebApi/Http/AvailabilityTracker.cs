using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 对端可用性跟踪（内部，P3-D8 / D13）。初始为 <see cref="WebApiAvailability.Unknown"/>。
/// 计为失败的传输层结果：连接中断、尝试超时、HTTP 502 / 503 / 504；收到其他任何响应即为 <see cref="WebApiAvailability.Online"/> 并清零；
/// 连续失败达到 <c>OfflineThreshold</c> 次即为 <see cref="WebApiAvailability.Offline"/>。用户取消和请求非法不影响状态，也不清零。
/// 状态变化经 <c>publish</c> 回调按发生顺序交出（回调在锁内调用，必须是非阻塞的入队）。
/// </summary>
internal sealed class AvailabilityTracker : IAvailabilityReporter
{
    private const int BadGateway = 502;
    private const int ServiceUnavailable = 503;
    private const int GatewayTimeout = 504;

    private readonly object sync = new object();
    private readonly string endpointName;
    private readonly int offlineThreshold;
    private readonly Action<WebApiAvailabilityChangedEventArgs> publish;
    private readonly ILogger logger;
    private WebApiAvailability current = WebApiAvailability.Unknown;
    private int consecutiveFailures;

    /// <summary>创建跟踪器。</summary>
    /// <param name="endpointName">端点名称。</param>
    /// <param name="offlineThreshold">判定离线所需的连续失败次数。</param>
    /// <param name="publish">交出状态变化事件的非阻塞回调。</param>
    /// <param name="logger">日志记录器。</param>
    public AvailabilityTracker(string endpointName, int offlineThreshold, Action<WebApiAvailabilityChangedEventArgs> publish, ILogger logger)
    {
        this.endpointName = endpointName;
        this.offlineThreshold = offlineThreshold;
        this.publish = publish;
        this.logger = logger;
    }

    /// <summary>当前的可用性。</summary>
    public WebApiAvailability Availability
    {
        get
        {
            lock (sync)
                return current;
        }
    }

    /// <inheritdoc />
    public void ReportAttempt(CommErrorKind transportKind, int? statusCode, Exception? exception)
    {
        if (transportKind == CommErrorKind.Cancelled || transportKind == CommErrorKind.InvalidRequest)
            return;

        lock (sync)
        {
            if (IsFailure(transportKind, statusCode))
            {
                consecutiveFailures++;
                if (consecutiveFailures >= offlineThreshold)
                    Change(WebApiAvailability.Offline, FailureReason(transportKind, statusCode), exception);
            }
            else if (statusCode.HasValue)
            {
                consecutiveFailures = 0;
                Change(WebApiAvailability.Online, CommErrorKind.None, null);
            }
        }
    }

    private static bool IsFailure(CommErrorKind kind, int? statusCode)
        => kind == CommErrorKind.ConnectionClosed || kind == CommErrorKind.Timeout
           || statusCode == BadGateway || statusCode == ServiceUnavailable || statusCode == GatewayTimeout;

    // 传输层失败用它自己的分类；502 / 503 / 504 是收到了响应，按状态码归类（对端错误）。
    private static CommErrorKind FailureReason(CommErrorKind kind, int? statusCode)
        => kind != CommErrorKind.None ? kind : HttpStatusClassifier.Classify(statusCode!.Value);

    private void Change(WebApiAvailability next, CommErrorKind reason, Exception? exception)
    {
        if (next == current)
            return;

        var args = new WebApiAvailabilityChangedEventArgs(endpointName, current, next, reason, exception);
        logger.LogInformation("The availability of {Endpoint} changed from {Previous} to {Current} ({Reason}).", endpointName, current, next, reason);
        current = next;
        publish(args);
    }
}
