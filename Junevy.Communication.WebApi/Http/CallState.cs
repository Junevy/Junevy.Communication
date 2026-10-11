using System.Diagnostics;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 一次调用在多次尝试之间共享的状态（内部）：调用序号、请求快照、拦截器共享的 Items、计时器与重试 / 重放计数。
/// 只由尝试循环及其单次尝试执行器读写，不跨线程共享。
/// </summary>
internal sealed class CallState
{
    /// <summary>创建调用状态并开始计时。</summary>
    /// <param name="callId">调用序号。</param>
    /// <param name="snapshot">请求快照。</param>
    /// <param name="requestUri">解析后的绝对请求 URI。</param>
    /// <param name="redactedUri">脱敏后的请求 URI（事件与日志中使用）。</param>
    public CallState(long callId, HttpRequestSnapshot snapshot, Uri requestUri, Uri redactedUri)
    {
        CallId = callId;
        Snapshot = snapshot;
        RequestUri = requestUri;
        RedactedUri = redactedUri;
    }

    /// <summary>调用序号：同一客户端内单调递增，从 1 开始。</summary>
    public long CallId { get; }

    /// <summary>请求快照。</summary>
    public HttpRequestSnapshot Snapshot { get; }

    /// <summary>解析后的绝对请求 URI（拦截器修改前）。</summary>
    public Uri RequestUri { get; }

    /// <summary>最近一次发出的尝试的脱敏 URI；还没有发出任何尝试时为快照 URI 的脱敏形式。</summary>
    public Uri RedactedUri { get; set; }

    /// <summary>本次调用内所有拦截器、所有尝试共享的数据。</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>整个调用的计时器（含重试的等待）。</summary>
    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    /// <summary>已经开始的尝试次数（含被拦截器中止、没有发出的）；上下文里的 <c>Attempt</c> 即它。</summary>
    public int Attempts { get; set; }

    /// <summary>实际发出的尝试次数（<c>RequestCompleted.Attempts</c>）。</summary>
    public int SentAttempts { get; set; }

    /// <summary>已用掉的重试次数；拦截器请求的重放不计入。</summary>
    public int RetriesUsed { get; set; }

    /// <summary>是否已经重放过一次（每次调用最多 1 次）。</summary>
    public bool Replayed { get; set; }
}
