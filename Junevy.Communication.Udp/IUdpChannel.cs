using System.Net;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Udp;

/// <summary>
/// UDP 通道的公开接口（设计文档第 9 节）。定向模式（配置了 RemoteHost）下 <see cref="IByteChannel.SendAsync"/> 与
/// <see cref="IByteChannel.RequestAsync"/> 发往远端；非定向模式下使用本接口的 <see cref="SendToAsync"/> 与 <see cref="RequestToAsync"/>。
/// </summary>
public interface IUdpChannel : IClientChannel
{
    /// <summary>构造时传入的配置对象（通道的运行行为只依赖构造时的快照）。</summary>
    UdpChannelConfig Config { get; }

    /// <summary>已绑定时的本地端点；未连接时为 null。</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>向指定地址发送一个数据报。定向模式下也可以发往其他地址（单向发送，不接收其应答）。</summary>
    /// <param name="remote">目标地址（端口不能为 0）。</param>
    /// <param name="payload">负载；可以为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发送结果。</returns>
    Task<CommResult> SendToAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向指定地址发送请求并等待来自该地址的应答。定向模式下只能请求远端（其他地址的应答会被来源过滤丢弃），否则返回 <c>InvalidRequest</c>。
    /// </summary>
    /// <param name="remote">目标地址（端口不能为 0）。</param>
    /// <param name="payload">请求负载，不能为空。</param>
    /// <param name="options">超时与匹配器；为 null 时使用配置的 RequestTimeout。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应答或失败结果。</returns>
    Task<CommResult<byte[]>> RequestToAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                            CancellationToken cancellationToken = default);
}
