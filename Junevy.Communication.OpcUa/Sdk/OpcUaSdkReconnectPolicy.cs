using Junevy.Communication.Core.Results;
using Opc.Ua;
using Opc.Ua.Client;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// SDK 的重连策略外面的一层（P62-D2、P6-C11）：首次连接被服务器以身份或证书类原因拒绝时停止重试。
/// SDK 的 <c>ManagedSession</c> 把首次连接失败也当作断线，按策略无限重试，于是用户名或密码错时
/// <c>ConnectAsync</c> 会一直重试到 <c>ConnectTimeout</c>，调用方只能看到 <c>Timeout</c>（F34）。
/// 会话建立之后不再拦截：重连沿用 P62-D2 的无限重试。
/// </summary>
internal sealed class OpcUaSdkReconnectPolicy : IReconnectPolicy
{
    private readonly ReconnectPolicy inner;
    private volatile bool established;

    /// <summary>按选项构造：指数退避、抖动 0.2、不限次数（P62-D2）。</summary>
    /// <param name="options">已校验的选项。</param>
    public OpcUaSdkReconnectPolicy(OpcUaClientOptions options)
        => inner = new ReconnectPolicy(new ReconnectPolicyOptions
        {
            InitialDelay = TimeSpan.FromMilliseconds(options.ReconnectInterval),
            MaxDelay = TimeSpan.FromMilliseconds(options.MaxReconnectInterval),
            JitterFactor = 0.2,
            MaxRetries = -1,
            Strategy = BackoffStrategy.Exponential,
        });

    /// <summary>会话已经建立过：此后的失败都按断线重连处理。</summary>
    public void MarkEstablished() => established = true;

    /// <inheritdoc/>
    public TimeSpan? GetNextDelay(int attempt, CancellationToken ct) => inner.GetNextDelay(attempt, ct);

    /// <inheritdoc/>
    public bool TryGetNextDelay(int attempt, StatusCode lastStatus, TimeSpan? serverRetryAfter, out TimeSpan? delay, CancellationToken ct)
    {
        // 分类只问错误映射这一处（P6-C7）：会被映射成 AuthenticationFailed 的状态重试也不会变好。
        if (!established
            && StatusCode.IsBad(lastStatus)
            && OpcUaSdkErrorMap.KindOf(lastStatus.Code) == CommErrorKind.AuthenticationFailed)
        {
            delay = null;
            return true;
        }

        return inner.TryGetNextDelay(attempt, lastStatus, serverRetryAfter, out delay, ct);
    }

    /// <inheritdoc/>
    public void Reset() => inner.Reset();
}
