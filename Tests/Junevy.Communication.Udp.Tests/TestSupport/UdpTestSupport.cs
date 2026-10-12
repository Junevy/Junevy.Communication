using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Udp.Tests;

/// <summary>UDP 测试的配置工厂：所有套接字绑定回环地址（计划第 1 节与任务书的测试环境约定）。</summary>
internal static class UdpTestConfig
{
    /// <summary>定向模式：绑定回环地址的随机端口，远端为给定的对端。</summary>
    public static UdpChannelConfig Directed(ScriptedUdpPeer peer, int requestTimeout = 3000)
        => new UdpChannelConfig
        {
            LocalAddress = "127.0.0.1",
            RemoteHost = "127.0.0.1",
            RemotePort = peer.EndPoint.Port,
            RequestTimeout = requestTimeout,
        };

    /// <summary>非定向模式：只绑定回环地址的随机端口。</summary>
    public static UdpChannelConfig Unconnected(int requestTimeout = 3000)
        => new UdpChannelConfig
        {
            LocalAddress = "127.0.0.1",
            RequestTimeout = requestTimeout,
        };
}

/// <summary>取得一个当前没有监听者的回环端口（绑定后立即释放）。</summary>
internal static class ClosedPort
{
    public static int Reserve()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
