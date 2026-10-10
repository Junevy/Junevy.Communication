using System.Net;

namespace Junevy.Communication.Tcp;

/// <summary>
/// 连接过滤器（设计文档 7.2）：服务端接受新连接之前调用，用于白名单之外的复杂规则。通过 <see cref="TcpChannelComponents.ConnectionFilter"/> 注入。
/// 实现必须线程安全，并且应当快速返回：它在接受循环上同步调用。
/// </summary>
public interface IConnectionFilter
{
    /// <summary>判断是否接受来自该远端地址的连接。</summary>
    /// <param name="remote">远端端点。</param>
    /// <returns>接受返回 true；拒绝返回 false（连接立即关闭）。</returns>
    bool Accept(IPEndPoint remote);
}
