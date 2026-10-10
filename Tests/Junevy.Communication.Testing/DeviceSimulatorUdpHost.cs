using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Testing;

/// <summary>
/// 以 UDP 承载的模拟设备，由 <see cref="DeviceSimulator.HostUdp"/> 创建。数据报不分帧：每个入站数据报就是一个完整请求。
/// 处理器返回的非 null 字节原样作为一个数据报回复给发送方；处理器抛出异常时该数据报不回复，宿主继续运行。
/// </summary>
public sealed class DeviceSimulatorUdpHost : IAsyncDisposable
{
    private readonly UdpClient socket;
    private readonly Func<byte[], byte[]?> handler;
    private readonly Func<byte[], int>? replyDelay;
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    private readonly object pendingLock = new object();
    private readonly List<Task> delayedReplies = new List<Task>();
    private readonly Task receiveLoop;
    private volatile bool disposed;

    internal DeviceSimulatorUdpHost(Func<byte[], byte[]?> handler, Func<byte[], int>? replyDelay)
    {
        this.handler = handler;
        this.replyDelay = replyDelay;

        socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endPoint = socket.Client.LocalEndPoint as IPEndPoint
            ?? throw new InvalidOperationException("The socket is not bound to an IP endpoint.");
        Port = endPoint.Port;

        receiveLoop = ReceiveLoopAsync();
    }

    /// <summary>设备监听的端口（回环地址，随机分配）。</summary>
    public int Port { get; }

    /// <summary>停止接收，并等待已排队的延迟回复结束。可重复调用。</summary>
    /// <returns>释放完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        shutdown.Cancel();
        socket.Dispose();
        await receiveLoop.ConfigureAwait(false);

        Task[] pending;
        lock (pendingLock)
            pending = delayedReplies.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        shutdown.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        while (!disposed)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await socket.ReceiveAsync().ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // 例如 ICMP 端口不可达报告到达接收端：忽略这一次错误，继续接收。
                continue;
            }
            catch (Exception)
            {
                // 套接字已释放或不可用：结束接收循环。
                return;
            }

            byte[]? reply = Respond(datagram.Buffer);
            if (reply is null)
                continue;

            int delay = replyDelay?.Invoke(datagram.Buffer) ?? 0;
            if (delay > 0)
                TrackDelayedReply(reply, datagram.RemoteEndPoint, delay);
            else
                await TrySendAsync(reply, datagram.RemoteEndPoint).ConfigureAwait(false);
        }
    }

    // 处理器抛出异常：设备忽略这个数据报，宿主继续运行（测试替身的故障不应终止整个宿主）。
    private byte[]? Respond(byte[] request)
    {
        try
        {
            return handler(request);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TrackDelayedReply(byte[] reply, IPEndPoint destination, int delay)
    {
        Task task = SendAfterAsync(reply, destination, delay);
        lock (pendingLock)
            delayedReplies.Add(task);
    }

    private async Task SendAfterAsync(byte[] reply, IPEndPoint destination, int delay)
    {
        try
        {
            await Task.Delay(delay, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 宿主已释放：不再发送。
            return;
        }

        await TrySendAsync(reply, destination).ConfigureAwait(false);
    }

    // 发送失败（例如宿主正在释放）只影响这一个回复。
    private async Task TrySendAsync(byte[] reply, IPEndPoint destination)
    {
        try
        {
            await socket.SendAsync(reply, reply.Length, destination).ConfigureAwait(false);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
