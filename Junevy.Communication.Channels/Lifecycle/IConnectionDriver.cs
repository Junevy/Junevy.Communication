using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 连接驱动：负责一条连接的打开（链路 + 握手）与关闭。由具体传输实现，字节流通道的实现为 <c>StreamConnectionDriver</c>。
/// 驱动只被 <see cref="ConnectionSupervisor"/> 在生命周期锁下调用，因此实现无需考虑与自身的并发调用。
/// </summary>
internal interface IConnectionDriver
{
    /// <summary>
    /// 打开链路并执行握手。失败时返回失败结果（驱动自行清理已创建的资源）；用户取消抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（用户取消或释放时触发）。</param>
    /// <returns>打开结果。</returns>
    Task<CommResult> OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 关闭当前连接；没有连接时为空操作。
    /// </summary>
    /// <param name="reason">关闭原因。</param>
    /// <param name="drainTimeout">优雅关闭的等待时间（毫秒）；0 表示不等待。</param>
    /// <returns>关闭完成的任务。</returns>
    Task CloseAsync(DisconnectReason reason, int drainTimeout);
}
