using System.Net;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Tcp;

/// <summary>
/// 服务端的一个 TCP 会话（设计文档 7.2）。会话是字节通道：可以收发帧、发起请求，并经 <see cref="IByteChannel.FrameReceived"/> 接收未被认领的帧。
/// </summary>
public interface ITcpSession : IByteChannel
{
    /// <summary>会话 ID（服务端内自增）。</summary>
    long Id { get; }

    /// <summary>对端端点。</summary>
    IPEndPoint RemoteEndPoint { get; }

    /// <summary>本端端点。</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>握手完成的时间（UTC）；握手完成之前为默认值。</summary>
    DateTimeOffset ConnectedAt { get; }

    /// <summary>会话是否可用：握手完成且未关闭时为 true。会话不实现 <see cref="IConnectable"/>，因此自行声明。</summary>
    bool IsConnected { get; }

    /// <summary>当前会话是否已完成 TLS 认证（认证成功后为 true，会话拆除后为 false）。</summary>
    bool IsTlsActive { get; }

    /// <summary>会话的连接统计。</summary>
    ConnectionStatistics Statistics { get; }

    /// <summary>会话的用户数据字典（线程安全），由宿主自行使用，例如保存登录用户名。</summary>
    IDictionary<string, object?> Items { get; }

    /// <summary>
    /// 关闭会话（原因 <see cref="DisconnectReason.UserRequested"/>）。在事件处理器内调用时只发出关闭信号，不等待，因此不会死锁。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；取消只停止等待，关闭仍会继续。</param>
    /// <returns>关闭完成的任务（在处理器内调用时立即完成）。</returns>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
