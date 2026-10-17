using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 被摘出去的处理器（内部，P32-D8）：响应已经不再等待它（超时或对端断开）。它之后的结果丢弃，任务的异常在这里被观察，避免成为未观察的任务异常。
/// </summary>
internal static class AbandonedWork
{
    /// <summary>观察被摘出去的处理器。</summary>
    /// <param name="invocation">处理器的任务。</param>
    /// <param name="requestId">请求编号。</param>
    /// <param name="reason">被摘出去的原因，写入日志。</param>
    /// <param name="logger">日志记录器。</param>
    public static void Observe(Task<ServerResult> invocation, long requestId, string reason, ILogger logger)
    {
        if (!invocation.IsCompleted)
            logger.LogWarning("The handler for request {RequestId} is still running after {Reason}; its result will be discarded.", requestId, reason);

        _ = invocation.ContinueWith(
            task => logger.LogDebug(task.Exception, "The handler for request {RequestId} finished after it was abandoned; its result was discarded.", requestId),
            TaskContinuationOptions.ExecuteSynchronously);
    }
}
