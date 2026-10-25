using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>取得回环地址上一个当前空闲的端口。<c>TcpServer</c> 的端口不接受 0，因此不能像 UDP 那样让系统选择。</summary>
internal static class FreePort
{
    public static int Get()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
