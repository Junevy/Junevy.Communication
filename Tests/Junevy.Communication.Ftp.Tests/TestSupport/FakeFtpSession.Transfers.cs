using System.Net.Sockets;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>文件传输命令：RETR（下载）与 STOR（上传）。数据经被动连接传输，TLS 时数据连接同样加密。</summary>
internal sealed partial class FakeFtpSession
{
    private async Task RetrieveAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(argument);
        if (!server.FileSystem.FileExists(target))
        {
            await ReplyAsync(550, "No such file.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await TransferAsync(server.FileSystem.ReadFile(target), cancellationToken).ConfigureAwait(false);
    }

    private async Task StoreAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(argument);
        if (!server.FileSystem.DirectoryExists(FakeFtpFileSystem.ParentOf(target)))
        {
            await ReplyAsync(550, "No such directory.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReceiveAsync(target, cancellationToken).ConfigureAwait(false);
    }

    // 接收客户端经数据连接发来的全部内容（直到它关闭连接），写入文件系统，最后应答 226。
    private async Task ReceiveAsync(string target, CancellationToken cancellationToken)
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

        await ReplyAsync(150, "Ready to receive data.", cancellationToken).ConfigureAwait(false);
        passiveListener = null;
        try
        {
            using TcpClient? data = await AcceptDataAsync(listener, cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                await ReplyAsync(425, "Can't open data connection.", cancellationToken).ConfigureAwait(false);
                return;
            }

            using Stream stream = await OpenDataStreamAsync(data, cancellationToken).ConfigureAwait(false);
            var received = new MemoryStream();
            await stream.CopyToAsync(received, cancellationToken).ConfigureAwait(false);
            server.FileSystem.WriteFile(target, received.ToArray(), DateTime.UtcNow);
            ThrowIfDropBeforeTransferReply();
            await ReplyAsync(226, "Transfer complete.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }
}
