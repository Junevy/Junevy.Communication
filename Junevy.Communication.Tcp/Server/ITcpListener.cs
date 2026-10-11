using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 服务端的监听器抽象（P25 Task 13，P25-D14）：接受循环只依赖此接口。测试可以注入会失败的实现，生产代码不再包含测试钩子。
/// </summary>
internal interface ITcpListener : IDisposable
{
    /// <summary>已绑定的本地端点；监听器已关闭时可能为 null。</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>接受一个连接。监听器被关闭或发生故障时抛出。</summary>
    /// <returns>已接受的套接字。</returns>
    Task<Socket> AcceptAsync();
}

/// <summary>
/// 绑定监听器的工厂（P25 Task 13）：按服务端配置与名称绑定；失败返回失败结果（消息文本由工厂给出）。
/// 默认工厂为 <see cref="SocketTcpListener.Bind"/>；测试通过内部构造函数注入其他实现。
/// </summary>
/// <param name="options">服务端的已校验选项。</param>
/// <param name="name">服务端名称（用于失败消息）。</param>
/// <returns>监听器，或失败结果。</returns>
internal delegate CommResult<ITcpListener> TcpListenerFactory(TcpServerOptions options, string name);
