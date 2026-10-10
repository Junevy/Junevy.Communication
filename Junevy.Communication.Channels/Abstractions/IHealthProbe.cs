using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels;

/// <summary>
/// 应用层心跳探测。内置实现发送心跳包（可选等待应答）；协议可以替换为自己的探测
/// （例如 MC 回环测试 0619、S7 读 SZL、HSMS Linktest）。
/// </summary>
public interface IHealthProbe
{
    /// <summary>执行一次探测。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>健康返回成功结果；否则返回失败结果。</returns>
    Task<CommResult> ProbeAsync(CancellationToken cancellationToken);
}
