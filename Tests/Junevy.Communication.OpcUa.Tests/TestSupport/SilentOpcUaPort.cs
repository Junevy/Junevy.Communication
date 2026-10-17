using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.OpcUa.Tests.TestSupport;

/// <summary>
/// 接受 TCP 连接但从不回应的端口（计划第 7 节第 2 条的"超时"用例）：用来证明
/// <c>ConnectTimeout</c> 是本库自己开的 <c>TimeoutScope</c> 兜住的（SDK 的 <c>ConnectAsync</c> 没有超时参数，F2）。
/// </summary>
public sealed class SilentOpcUaPort : IDisposable
{
    private readonly TcpListener listener;
    private readonly List<TcpClient> accepted = new();
    private int disposed;

    /// <summary>占用一个空闲端口并开始监听。</summary>
    public SilentOpcUaPort()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(AcceptAsync);
        EndpointUrl = $"opc.tcp://127.0.0.1:{Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/UA/";
    }

    /// <summary>端点地址。</summary>
    public string EndpointUrl { get; }

    /// <summary>实际端口。</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>停止监听并断开已接受的连接。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        listener.Stop();
        foreach (TcpClient client in accepted)
        {
            client.Dispose();
        }

        accepted.Clear();
    }

    private async Task AcceptAsync()
    {
        while (Volatile.Read(ref disposed) == 0)
        {
            try
            {
                accepted.Add(await listener.AcceptTcpClientAsync().ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
        }
    }
}
