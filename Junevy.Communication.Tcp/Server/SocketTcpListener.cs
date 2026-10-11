using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 包装 <see cref="Socket"/> 的默认监听器（P25 Task 13）。<see cref="Bind"/> 是默认的 <see cref="TcpListenerFactory"/>。
/// </summary>
internal sealed class SocketTcpListener : ITcpListener
{
    private readonly Socket socket;

    /// <summary>包装已绑定并处于监听状态的套接字。</summary>
    /// <param name="socket">监听套接字；由本对象释放。</param>
    public SocketTcpListener(Socket socket)
    {
        this.socket = socket;
    }

    /// <inheritdoc />
    public IPEndPoint? LocalEndPoint => socket.LocalEndPoint as IPEndPoint;

    /// <inheritdoc />
    public Task<Socket> AcceptAsync() => AcceptSocketAsync(socket);

    /// <inheritdoc />
    public void Dispose() => socket.Dispose();

    /// <summary>
    /// 绑定 TCP 监听套接字（默认工厂）。端口占用映射为 ResourceExhausted，其他套接字错误映射为 ConnectionClosed（计划 11.2）。
    /// </summary>
    /// <param name="options">服务端的已校验选项。</param>
    /// <param name="name">服务端名称（用于失败消息）。</param>
    /// <returns>监听器，或失败结果。</returns>
    internal static CommResult<ITcpListener> Bind(TcpServerOptions options, string name)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(options.ListenAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ConfigureListener(socket, options);
            socket.Bind(new IPEndPoint(options.ListenAddress, options.Port));
            socket.Listen(options.Backlog);

            Socket bound = socket;
            socket = null;
            return CommResult<ITcpListener>.Success(new SocketTcpListener(bound));
        }
        catch (SocketException ex)
        {
            CommErrorKind kind = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? CommErrorKind.ResourceExhausted
                : CommErrorKind.ConnectionClosed;
            return CommResult<ITcpListener>.Fail($"Listening on {name} failed: {ex.SocketErrorCode}.", kind, null, ex);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    // Windows 上使用独占地址（端口被占用时绑定失败，而不是与其他套接字共享）；其他平台允许地址复用，便于重启后立即重新绑定。
    private static void ConfigureListener(Socket socket, TcpServerOptions options)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            socket.ExclusiveAddressUse = true;
        else
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (options.Socket.ReceiveBufferSize > 0)
            socket.ReceiveBufferSize = options.Socket.ReceiveBufferSize;
        if (options.Socket.SendBufferSize > 0)
            socket.SendBufferSize = options.Socket.SendBufferSize;
    }

#if NET8_0_OR_GREATER
    private static async Task<Socket> AcceptSocketAsync(Socket listener)
        => await listener.AcceptAsync().ConfigureAwait(false);
#else
    // net472 的 Socket 没有 AcceptAsync：以 BeginAccept/EndAccept 包装。停止时关闭监听器即可打断挂起的接受。
    private static Task<Socket> AcceptSocketAsync(Socket listener)
        => Task.Factory.FromAsync(listener.BeginAccept(null, null), listener.EndAccept);
#endif
}
