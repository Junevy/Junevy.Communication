using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp.Server;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 测试注入的监听器（替代已删除的 <c>TcpServer.SimulateListenerFault</c>，P25-D14）：第一次接受连接时失败，模拟监听器故障；
/// 之后的接受与释放都委托给真实监听器。
/// </summary>
internal sealed class FaultOnFirstAcceptListener : ITcpListener
{
    private readonly ITcpListener inner;
    private int accepts;

    private FaultOnFirstAcceptListener(ITcpListener inner)
    {
        this.inner = inner;
    }

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint => inner.LocalEndPoint;

    /// <inheritdoc />
    public Task<Socket> AcceptAsync()
    {
        if (Interlocked.Increment(ref accepts) == 1)
            return Task.FromException<Socket>(new IOException("The injected listener fault."));

        return inner.AcceptAsync();
    }

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();

    /// <summary>
    /// 创建监听器工厂：第一次绑定得到上面的失败监听器，之后的绑定（例如故障后重新监听）都是真实的。
    /// </summary>
    /// <returns>监听器工厂。</returns>
    public static TcpListenerFactory CreateFactory()
    {
        int binds = 0;
        return (options, name) =>
        {
            int attempt = Interlocked.Increment(ref binds);
            CommResult<ITcpListener> bound = SocketTcpListener.Bind(options, name);
            if (!bound.IsSuccess || attempt != 1)
                return bound;

            return CommResult<ITcpListener>.Success(new FaultOnFirstAcceptListener(bound.Data!));
        };
    }
}
