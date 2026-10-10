using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels;

/// <summary>
/// 拥有一条字节链路的对象的链路状态。通道和地址型协议客户端都实现它，语义都是"底层链路是否可用"。
/// </summary>
public interface IConnectable
{
    /// <summary>实例名称（与注册表中的名称一致）。</summary>
    string Name { get; }

    /// <summary>当前连接状态。</summary>
    ConnectionState State { get; }

    /// <summary>当前是否处于 <see cref="ConnectionState.Connected"/>。</summary>
    bool IsConnected { get; }

    /// <summary>连接统计；属性读取线程安全。</summary>
    ConnectionStatistics Statistics { get; }

    /// <summary>状态变化事件；按顺序、在锁外触发。</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 建立连接。失败时返回失败结果；用户取消时抛出 <see cref="OperationCanceledException"/>（与 IModbus.ConnectAsync 一致）。
    /// </summary>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>连接结果。</returns>
    Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 用户主动断开：停止后台重连，此后不会自动连回。
    /// </summary>
    /// <param name="cancellationToken">用户取消令牌。</param>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 等待进入 <see cref="ConnectionState.Connected"/>。
    /// </summary>
    /// <param name="timeout">超时毫秒数。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>进入 Connected 返回 true；超时返回 false。</returns>
    Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken = default);
}
