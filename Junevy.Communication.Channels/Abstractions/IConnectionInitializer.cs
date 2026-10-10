using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels;

/// <summary>
/// 握手钩子：链路连通后、状态变为 Connected 之前执行；每次重连都会重新执行。
/// 服务端对每个新会话执行同一接口（例如 HSMS 被动端等待 Select.req 并应答）。
/// </summary>
public interface IConnectionInitializer
{
    /// <summary>执行握手。失败时通道断开并返回失败结果。</summary>
    /// <param name="channel">绑定到这条新连接的字节通道视图（此时尚未进入 Connected）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>握手结果。</returns>
    Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken);
}
