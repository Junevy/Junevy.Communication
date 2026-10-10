namespace Junevy.Communication.Core.Utils;

/// <summary>
/// 链接"用户令牌 + 超时"的作用域。区分超时与用户取消（<see cref="IsTimedOut"/> / <see cref="IsUserCancelled"/>）。
/// 超时或取消时，<c>onAbort</c> 至多调用一次，用于中止不响应取消令牌的 I/O（例如销毁 socket）；
/// <see cref="Dispose"/> 之后不再调用。
/// </summary>
public sealed class TimeoutScope : IDisposable
{
    private const int StateActive = 0;
    private const int StateAborted = 1;
    private const int StateDisposed = 2;

    private readonly CancellationToken userToken;
    private readonly CancellationTokenSource? timeoutSource;
    private readonly CancellationTokenSource linkedSource;
    private readonly CancellationToken token;
    private readonly CancellationTokenRegistration registration;
    private readonly Action? onAbort;
    private int state = StateActive;

    private TimeoutScope(int timeout, CancellationToken userToken, Action? onAbort)
    {
        this.userToken = userToken;
        this.onAbort = onAbort;

        if (timeout > 0)
        {
            timeoutSource = new CancellationTokenSource(timeout);
            linkedSource = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutSource.Token);
        }
        else
        {
            linkedSource = CancellationTokenSource.CreateLinkedTokenSource(userToken);
        }

        token = linkedSource.Token;

        // 若令牌已经取消，Register 会同步执行回调；此时所有字段都已初始化。
        registration = token.Register(OnCancelled);
    }

    /// <summary>
    /// 启动作用域。
    /// </summary>
    /// <param name="timeout">超时毫秒数；≤ 0 表示不限时。</param>
    /// <param name="userToken">用户取消令牌。</param>
    /// <param name="onAbort">超时或用户取消时调用一次（例如销毁 socket）；可为 null。</param>
    /// <returns>超时作用域；调用方负责 <see cref="Dispose"/>。</returns>
    public static TimeoutScope Start(int timeout, CancellationToken userToken, Action? onAbort = null)
        => new TimeoutScope(timeout, userToken, onAbort);

    /// <summary>链接后的令牌：超时或用户取消时触发。</summary>
    public CancellationToken Token => token;

    /// <summary>超时已触发，且用户令牌未触发。</summary>
    public bool IsTimedOut
        => timeoutSource != null && timeoutSource.IsCancellationRequested && !userToken.IsCancellationRequested;

    /// <summary>用户令牌已触发。</summary>
    public bool IsUserCancelled => userToken.IsCancellationRequested;

    /// <summary>
    /// 释放作用域：取消计时器与令牌链接。若 <c>onAbort</c> 尚未调用，之后也不再调用。可重复调用。
    /// </summary>
    public void Dispose()
    {
        // 先占用状态，阻止之后的 onAbort 调用；若已因超时或取消而中止，保持该状态。
        Interlocked.CompareExchange(ref state, StateDisposed, StateActive);
        registration.Dispose();
        linkedSource.Dispose();
        timeoutSource?.Dispose();
    }

    private void OnCancelled()
    {
        if (Interlocked.CompareExchange(ref state, StateAborted, StateActive) != StateActive)
            return;

        try
        {
            onAbort?.Invoke();
        }
        catch (Exception)
        {
            // 中止是尽力而为：异常不能打断取消流程（计时器线程上未处理的异常会终止进程）。
        }
    }
}
