using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 被摘出去的处理器（内部，P32-D8）：响应已经不再等待它（超时或对端断开），它之后的结果丢弃。
/// 这些处理器已经让出了并发许可，却仍占着线程与设备；这里统计仍在运行的数量（服务器侧可观测），并在 Warning 里写出，
/// 使"设备卡住、MES 反复重发"造成的堆积看得见。任务的异常也在这里被观察，避免成为未观察的任务异常。
/// </summary>
internal sealed class AbandonedWork
{
    private readonly ILogger logger;
    private int running;

    /// <summary>创建统计。</summary>
    /// <param name="logger">日志记录器。</param>
    public AbandonedWork(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>仍在运行的被摘出去的处理器数。</summary>
    public int Count => Volatile.Read(ref running);

    /// <summary>观察被摘出去的处理器。</summary>
    /// <param name="invocation">处理器的任务。</param>
    /// <param name="requestId">请求编号。</param>
    /// <param name="reason">被摘出去的原因，写入日志。</param>
    public void Observe(Task<ServerResult> invocation, long requestId, string reason)
    {
        if (invocation.IsCompleted)
        {
            // 恰好在摘出之前结束：读取一次 Exception 即视为已观察。
            _ = invocation.Exception;
            return;
        }

        int count = Interlocked.Increment(ref running);
        logger.LogWarning("The handler for request {RequestId} is still running after {Reason}; its result will be discarded ({Count} abandoned handler(s) running).", requestId, reason, count);
        _ = invocation.ContinueWith(
            task =>
            {
                Interlocked.Decrement(ref running);
                logger.LogDebug(task.Exception, "The handler for request {RequestId} finished after it was abandoned; its result was discarded.", requestId);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
