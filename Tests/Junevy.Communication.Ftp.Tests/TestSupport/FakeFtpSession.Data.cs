using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>被动模式（PASV 与 EPSV）的应答与数据监听；数据传输命令在 Task 4、5 加入。</summary>
internal sealed partial class FakeFtpSession
{
    private const int PortHighByteDivisor = 256;

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
