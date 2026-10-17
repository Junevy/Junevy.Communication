namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 在途请求计数（内部，Q28）：服务器侧可观测的并发数，供测试断言限流与停止的行为，不只看 HTTP 响应。
/// </summary>
internal sealed class InFlightCounter
{
    private int count;

    /// <summary>当前在途的请求数。</summary>
    public int Count => Volatile.Read(ref count);

    /// <summary>一个请求进入。</summary>
    public void Enter() => Interlocked.Increment(ref count);

    /// <summary>一个请求结束。</summary>
    public void Exit() => Interlocked.Decrement(ref count);
}
