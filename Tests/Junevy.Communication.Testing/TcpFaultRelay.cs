using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Testing;

/// <summary>
/// 客户端与第三方设备软件之间的 TCP 中继，用来制造链路故障：不必动设备软件，设备看到的就是真实的断连与新连接。
/// 原为 Modbus 互通测试所写，S7 互通测试共用后移入本库。
/// </summary>
public sealed class TcpFaultRelay : IAsyncDisposable
{
    private const int BufferSize = 1024;

    private readonly string targetHost;
    private readonly int targetPort;
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly object sync = new object();
    private readonly List<TcpClient> sockets = new List<TcpClient>();
    private readonly Task acceptLoop;
    private int connections;

    /// <summary>在回环地址的随机端口上开始监听，每个接入的连接都转发到目标地址。</summary>
    /// <param name="targetHost">设备的主机名或地址。</param>
    /// <param name="targetPort">设备的端口。</param>
    public TcpFaultRelay(string targetHost, int targetPort)
    {
        this.targetHost = targetHost;
        this.targetPort = targetPort;
        listener.Start();
        acceptLoop = AcceptLoopAsync();
    }

    /// <summary>中继监听的端口。</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>累计接受的连接数。</summary>
    public int Connections => Volatile.Read(ref connections);

    /// <summary>为 true 时丢弃两个方向的数据，连接保持（模拟网络黑洞）。</summary>
    public volatile bool Blackhole;

    /// <summary>设备应答在中继里停留的毫秒数（模拟迟到的应答）。</summary>
    public volatile int ReplyDelay;

    /// <summary>断开当前所有连接（模拟网线或设备侧断开）。</summary>
    public void DropConnections()
    {
        lock (sync)
        {
            foreach (TcpClient socket in sockets)
                socket.Dispose();
            sockets.Clear();
        }
    }

    /// <summary>停止监听并断开所有连接。</summary>
    /// <returns>表示释放完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        listener.Stop();
        DropConnections();
        await acceptLoop.ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                Interlocked.Increment(ref connections);
                _ = LinkAsync(client);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // 监听器已停止。
        }
    }

    private async Task LinkAsync(TcpClient client)
    {
        var server = new TcpClient();
        lock (sync)
        {
            sockets.Add(client);
            sockets.Add(server);
        }

        try
        {
            await server.ConnectAsync(targetHost, targetPort).ConfigureAwait(false);
            await Task.WhenAny(PumpAsync(client, server, delayed: false), PumpAsync(server, client, delayed: true)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 任一侧断开都按断连处理。
        }

        client.Dispose();
        server.Dispose();
    }

    private async Task PumpAsync(TcpClient from, TcpClient to, bool delayed)
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                int read = await from.GetStream().ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0)
                    return;

                int delay = ReplyDelay;
                if (delayed && delay > 0)
                    await Task.Delay(delay).ConfigureAwait(false);

                if (!Blackhole)
                    await to.GetStream().WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // 任一侧断开都结束转发。
        }
    }
}
