using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Udp.Tests;

/// <summary>
/// 需要本机支持组播回环的测试。构造期间探测：创建套接字、加入 239.255.0.1、开启回环；任一步失败时设置 <see cref="FactAttribute.Skip"/>，
/// 并在跳过原因中写明异常信息。探测失败只跳过测试，不放宽断言。
/// </summary>
public sealed class MulticastFactAttribute : FactAttribute
{
    /// <summary>创建特性；本机不支持组播回环时跳过该测试。</summary>
    public MulticastFactAttribute()
    {
        string? failure = ProbeMulticast();
        if (failure != null)
            Skip = $"Multicast loopback is not available on this machine: {failure}";
    }

    private static string? ProbeMulticast()
    {
        try
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            socket.JoinMulticastGroup(IPAddress.Parse("239.255.0.1"));
            socket.MulticastLoopback = true;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
