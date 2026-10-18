using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>被动模式（PASV 与 EPSV）的应答与数据监听；数据传输命令在 Task 4、5 加入。</summary>
internal sealed partial class FakeFtpSession
{
    private const int PortHighByteDivisor = 256;

    // 数据连接要在这段时间内建立，否则视为被拒绝（425）。
    private static readonly TimeSpan DataAcceptTimeout = TimeSpan.FromSeconds(5);

    // 数据传输的统一入口：被拒绝时按故障注入返回应答码（不发 150）；否则 150、经被动连接发送整段内容、226；
    // 注入了中断字节数时，发送到该字节数后关闭数据连接并应答 426。
    private async Task TransferAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (server.Faults.DataRejectCode != 0)
        {
            await ReplyAsync(server.Faults.DataRejectCode, "Can't open data connection.", cancellationToken).ConfigureAwait(false);
            return;
        }

        TcpListener? listener = passiveListener;
        if (listener is null)
        {
            await ReplyAsync(425, "Use PASV or EPSV first.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(150, "Opening data connection.", cancellationToken).ConfigureAwait(false);
        passiveListener = null;
        try
        {
            await SendOverDataConnectionAsync(listener, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task SendOverDataConnectionAsync(TcpListener listener, byte[] payload, CancellationToken cancellationToken)
    {
        using TcpClient? data = await AcceptDataAsync(listener, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            await ReplyAsync(425, "Can't open data connection.", cancellationToken).ConfigureAwait(false);
            return;
        }

        long abortAfter = server.Faults.AbortTransferAfterBytes;
        int count = abortAfter < 0 ? payload.Length : (int)Math.Min(payload.Length, abortAfter);
        Stream dataStream = data.GetStream();
        if (dataProtected)
        {
            var ssl = new System.Net.Security.SslStream(dataStream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(
                new System.Net.Security.SslServerAuthenticationOptions { ServerCertificate = server.Certificate },
                cancellationToken).ConfigureAwait(false);
            dataStream = ssl;
        }

        await dataStream.WriteAsync(payload.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        await dataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await dataStream.DisposeAsync().ConfigureAwait(false);

        if (abortAfter >= 0 && count < payload.Length)
        {
            await ReplyAsync(426, "Connection closed; transfer aborted.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(226, "Transfer complete.", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<TcpClient?> AcceptDataAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DataAcceptTimeout);
        try
        {
            return await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task PassiveAsync(bool extended, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        passiveListener?.Stop();
        passiveListener = new TcpListener(IPAddress.Loopback, 0);
        passiveListener.Start();
        int port = ((IPEndPoint)passiveListener.LocalEndpoint).Port;

        if (extended)
        {
            await ReplyAsync(229, $"Entering Extended Passive Mode (|||{port}|)", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            int high = port / PortHighByteDivisor;
            int low = port % PortHighByteDivisor;
            await ReplyAsync(227, $"Entering Passive Mode (127,0,0,1,{high},{low}).", cancellationToken).ConfigureAwait(false);
        }
    }
}
