using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Udp;

/// <summary>
/// 基于 <see cref="UdpClient"/> 的数据报传输（内部）。打开时完成全部套接字配置：SIO_UDP_CONNRESET、SO_REUSEADDR、接收缓冲、绑定、
/// 广播、组播（回环与 TTL）；定向模式在打开时解析远端（D13：不调用 Connect，来源过滤由 DatagramChannel 完成）。
/// </summary>
internal sealed class UdpDatagramTransport : IDatagramTransport
{
    // SIO_UDP_CONNRESET（设计文档第 9 节）：关闭"向无人监听的端口发送后，接收抛出 WSAECONNRESET（10054）"的行为，否则接收循环会被打断。
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly UdpClient client;
    private readonly EndPoint? directedRemote;
    private int aborted;

    private UdpDatagramTransport(UdpClient client, EndPoint? directedRemote)
    {
        this.client = client;
        this.directedRemote = directedRemote;
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint
    {
        get
        {
            if (Volatile.Read(ref aborted) != 0)
                return null;

            try
            {
                return client.Client.LocalEndPoint;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public EndPoint? DirectedRemote => directedRemote;

    /// <summary>
    /// 打开传输：解析定向模式的远端（受握手时限约束），创建并配置套接字。失败时释放已创建的套接字并返回失败结果；用户取消抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <param name="settings">已校验的套接字参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>打开的传输，或失败结果。</returns>
    public static async Task<CommResult<IDatagramTransport>> OpenAsync(UdpChannelSettings settings, CancellationToken cancellationToken)
    {
        UdpClient? created = null;
        try
        {
            created = new UdpClient(settings.LocalAddress.AddressFamily);
            Configure(created, settings);
        }
        catch (SocketException ex)
        {
            created?.Close();
            CommErrorKind kind = ex.SocketErrorCode == SocketError.TimedOut ? CommErrorKind.Timeout : CommErrorKind.ConnectionClosed;
            return CommResult<IDatagramTransport>.Fail($"Opening the UDP socket on {settings.LocalAddress}:{settings.LocalPort} failed: {ex.Message}",
                                                       kind, null, ex);
        }
        catch (Exception ex)
        {
            created?.Close();
            return CommResult<IDatagramTransport>.Fail("Opening the UDP socket failed unexpectedly.", CommErrorKind.Unspecified, null, ex);
        }

        // 定向模式最后解析远端（计划 15.2）。失败或取消时释放刚创建的套接字。
        UdpClient client = created!;
        EndPoint? remote = null;
        if (settings.RemoteHost != null)
        {
            CommResult<IPAddress> resolved;
            try
            {
                resolved = await ResolveRemoteAsync(settings.RemoteHost, settings.LocalAddress.AddressFamily,
                                                    settings.Client.HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                client.Close();
                throw;
            }

            if (!resolved.IsSuccess)
            {
                client.Close();
                return resolved.As<IDatagramTransport>();
            }

            remote = new IPEndPoint(resolved.Data!, settings.RemotePort);
        }

        return CommResult<IDatagramTransport>.Success(new UdpDatagramTransport(client, remote));
    }

    /// <inheritdoc />
    public async Task<DatagramReceipt> ReceiveAsync(CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        UdpReceiveResult received = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
#else
        // net472：UdpClient.ReceiveAsync() 没有取消重载。停止时由 Abort 释放 UdpClient，打断挂起的接收（计划 15.2）。
        UdpReceiveResult received = await client.ReceiveAsync().ConfigureAwait(false);
#endif
        return new DatagramReceipt(received.Buffer, received.RemoteEndPoint);
    }

    /// <inheritdoc />
    public Task SendToAsync(ReadOnlyMemory<byte> payload, EndPoint destination, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        return client.Client.SendToAsync(payload, SocketFlags.None, destination, cancellationToken).AsTask();
#else
        // net472：UdpClient.SendAsync 只接受数组；复制负载（数据报频率低，复制开销可以忽略）。
        return SendFrameworkAsync(payload.ToArray(), destination);
#endif
    }

    /// <inheritdoc />
    public void Abort()
    {
        if (Interlocked.Exchange(ref aborted, 1) == 0)
            client.Close();
    }

#if NETFRAMEWORK
    private async Task SendFrameworkAsync(byte[] datagram, EndPoint destination)
    {
        await client.SendAsync(datagram, datagram.Length, (IPEndPoint)destination).ConfigureAwait(false);
    }
#endif

    // 套接字配置（计划 15.2 的打开顺序）：SO_REUSEADDR → 接收缓冲 → 绑定 → 广播 → 组播回环与 TTL → 加入组播组 → SIO_UDP_CONNRESET。
    // 不调用 Connect（D13）。任何一步失败都抛出异常，由调用方释放套接字。
    private static void Configure(UdpClient client, UdpChannelSettings settings)
    {
        var socket = client.Client;
        if (settings.ReuseAddress)
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        socket.ReceiveBufferSize = settings.ReceiveBufferSize;
        socket.Bind(new IPEndPoint(settings.LocalAddress, settings.LocalPort));

        client.EnableBroadcast = settings.EnableBroadcast;
        client.MulticastLoopback = settings.MulticastLoopback;
        client.Ttl = settings.MulticastTimeToLive;
        foreach (IPAddress group in settings.MulticastGroups)
            client.JoinMulticastGroup(group);

        DisableConnectionReset(client);
    }

    // SIO_UDP_CONNRESET：net472 恒执行（.NET Framework 只在 Windows 上运行）；net8 仅在 Windows 上执行，其他平台不支持该控制码。
    private static void DisableConnectionReset(UdpClient client)
    {
#if NETFRAMEWORK
        client.Client.IOControl(SioUdpConnReset, new byte[4], null);
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            client.Client.IOControl(SioUdpConnReset, new byte[4], null);
#endif
    }

    // 解析定向模式的远端。IP 字面量直接使用；主机名经 DNS 解析，结果限定在本地套接字的地址族内，并受握手时限约束。
    private static async Task<CommResult<IPAddress>> ResolveRemoteAsync(string host, AddressFamily family, int timeout,
                                                                         CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            if (literal.AddressFamily == family)
                return CommResult<IPAddress>.Success(literal);

            return CommResult<IPAddress>.Fail($"The remote address {host} does not match the address family of LocalAddress.", CommErrorKind.InvalidRequest);
        }

        using TimeoutScope scope = TimeoutScope.Start(timeout, cancellationToken);
        IPAddress[] addresses;
        try
        {
            addresses = await LookupAsync(host, scope.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (scope.IsTimedOut)
                return CommResult<IPAddress>.Fail($"Resolving {host} did not complete within {timeout} ms.", CommErrorKind.Timeout, null, ex);

            return CommResult<IPAddress>.Fail($"Resolving {host} failed.", CommErrorKind.ConnectionClosed, null, ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IPAddress? match = addresses.FirstOrDefault(address => address.AddressFamily == family);
        if (match == null)
            return CommResult<IPAddress>.Fail($"No usable address was found for {host}.", CommErrorKind.ConnectionClosed);

        return CommResult<IPAddress>.Success(match);
    }

#if NET8_0_OR_GREATER
    private static Task<IPAddress[]> LookupAsync(string host, CancellationToken token)
        => Dns.GetHostAddressesAsync(host, token);
#else
    // net472 的 Dns 没有可取消的重载：与取消令牌竞速。令牌触发后丢弃解析结果，并观察其异常。
    private static async Task<IPAddress[]> LookupAsync(string host, CancellationToken token)
    {
        Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
        var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => interrupted.TrySetResult(true)))
        {
            Task finished = await Task.WhenAny(lookup, interrupted.Task).ConfigureAwait(false);
            if (!ReferenceEquals(finished, lookup))
            {
                _ = lookup.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                token.ThrowIfCancellationRequested();
            }
        }

        return await lookup.ConfigureAwait(false);
    }
#endif
}
