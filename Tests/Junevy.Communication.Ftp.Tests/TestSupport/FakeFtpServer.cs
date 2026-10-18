using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>
/// 进程内的 FTP 模拟服务器（P6-C12，计划 Task 3）。控制连接监听回环地址的随机端口。
/// 只实现 FluentFTP 实际发出的命令，其余命令返回 502，因此每一条实现的命令都由客户端的真实调用触发。
/// </summary>
public sealed class FakeFtpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly object sync = new();
    private readonly List<(FakeFtpSession Session, Task Run)> sessions = new();
    private Task acceptLoop = Task.CompletedTask;
    private int connectionCount;

    private FakeFtpServer(FakeFtpOptions options)
    {
        Options = options;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Certificate = options.Tls == FakeFtpTls.None ? null : TestCertificates.CreateSelfSigned("CN=localhost").Certificate;
    }

    /// <summary>启动模拟服务器并开始接受连接。</summary>
    /// <param name="options">行为开关；为 null 时使用默认值（用户 line1，密码 secret，明文）。</param>
    /// <returns>已在监听的服务器；用完后调用 <see cref="DisposeAsync"/>。</returns>
    public static FakeFtpServer Start(FakeFtpOptions? options = null)
    {
        var server = new FakeFtpServer(options ?? new FakeFtpOptions());
        server.acceptLoop = server.AcceptLoopAsync(server.stopping.Token);
        return server;
    }

    /// <summary>服务器监听的端口（回环地址）。</summary>
    public int Port { get; }

    /// <summary>行为开关。</summary>
    public FakeFtpOptions Options { get; }

    /// <summary>服务器的内存文件系统。</summary>
    public FakeFtpFileSystem FileSystem { get; } = new();

    /// <summary>故障注入的开关。</summary>
    public FakeFtpFaults Faults { get; } = new();

    /// <summary>服务器收到的命令及其应答。</summary>
    public FakeFtpCommandLog Commands { get; } = new();

    /// <summary>TLS 使用的服务器证书（只有 TLS 模式下存在）。</summary>
    public X509Certificate2? Certificate { get; }

    /// <summary>接受过的控制连接数。</summary>
    public int ConnectionCount => Volatile.Read(ref connectionCount);

    /// <summary>停止服务器：关闭监听与全部会话，等待它们结束。</summary>
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();

        List<(FakeFtpSession Session, Task Run)> snapshot;
        lock (sync)
        {
            snapshot = sessions.ToList();
        }

        foreach ((FakeFtpSession session, _) in snapshot)
        {
            session.Dispose();
        }

        var pending = snapshot.Select(s => s.Run).Append(acceptLoop).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        Certificate?.Dispose();
        stopping.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            int id = Interlocked.Increment(ref connectionCount);
            var session = new FakeFtpSession(this, tcp, id);
            Task run = session.RunAsync(cancellationToken);
            lock (sync)
            {
                sessions.Add((session, run));
            }
        }
    }
}
