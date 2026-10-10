using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Testing;

/// <summary>
/// 测试辅助 UDP 对端：监听回环地址的随机端口，对收到的每个数据报调用脚本；脚本返回非 null 时回复到来源地址。
/// </summary>
public sealed class ScriptedUdpPeer : IDisposable
{
    private readonly UdpClient client;
    private readonly Func<int, UdpReceiveResult, Task<byte[]?>> handler;
    private int receivedCount;
    private volatile bool disposed;

    private ScriptedUdpPeer(UdpClient client, IPEndPoint endPoint, Func<int, UdpReceiveResult, Task<byte[]?>> handler)
    {
        this.client = client;
        this.handler = handler;
        EndPoint = endPoint;
    }

    /// <summary>对端的监听地址（回环地址，随机端口）。</summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>已接收的数据报数量。</summary>
    public int ReceivedCount => Volatile.Read(ref receivedCount);

    /// <summary>
    /// 启动对端并开始接收数据报。
    /// </summary>
    /// <param name="handler">数据报处理脚本：参数为数据报序号（从 0 开始）与数据报；返回 null 表示不回复。</param>
    /// <returns>已启动的对端；调用方负责 <see cref="Dispose"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 null。</exception>
    public static ScriptedUdpPeer Start(Func<int, UdpReceiveResult, Task<byte[]?>> handler)
    {
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endPoint = client.Client.LocalEndPoint as IPEndPoint
            ?? throw new InvalidOperationException("The UDP socket is not bound to an IP endpoint.");

        var peer = new ScriptedUdpPeer(client, endPoint, handler);
        _ = peer.ReceiveLoopAsync();
        return peer;
    }

    /// <summary>从对端向指定地址发送一个数据报（例如模拟设备主动上报）。</summary>
    /// <param name="remote">目标地址。</param>
    /// <param name="payload">数据报内容。</param>
    /// <returns>发送完成的任务。</returns>
    public Task SendToAsync(IPEndPoint remote, byte[] payload)
    {
        if (remote == null)
            throw new ArgumentNullException(nameof(remote));
        if (payload == null)
            throw new ArgumentNullException(nameof(payload));

        return client.SendAsync(payload, payload.Length, remote);
    }

    /// <summary>关闭套接字，停止接收循环。可重复调用。</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        client.Close();
    }

    private async Task ReceiveLoopAsync()
    {
        while (!disposed)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await client.ReceiveAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Windows 上 UDP 可能因之前的 ICMP 不可达报告而抛出 SocketException，此时继续接收；Dispose 之后则退出。
                if (disposed)
                    break;

                continue;
            }

            int index = Interlocked.Increment(ref receivedCount) - 1;
            try
            {
                byte[]? reply = await handler(index, datagram).ConfigureAwait(false);
                if (reply != null)
                    await client.SendAsync(reply, reply.Length, datagram.RemoteEndPoint).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 脚本异常只影响这一个数据报；接收循环继续。
            }
        }
    }
}
