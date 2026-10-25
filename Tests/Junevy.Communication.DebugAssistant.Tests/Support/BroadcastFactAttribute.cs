using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 需要广播投递的测试（计划 Task 5 第 2 条）：发现测试时先探测本机能否把广播投递到本机的监听端口；
/// 不能时以 <see cref="FactAttribute.Skip"/> 跳过，并写明原因。
/// </summary>
public sealed class BroadcastFactAttribute : FactAttribute
{
    public BroadcastFactAttribute()
    {
        string? reason = BroadcastProbe.Unsupported();
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}

internal static class BroadcastProbe
{
    /// <summary>返回本机不能投递广播的原因；能投递时返回 null。</summary>
    public static string? Unsupported()
    {
        try
        {
            using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            listener.Client.ReceiveTimeout = 1000;
            int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

            using var sender = new UdpClient { EnableBroadcast = true };
            sender.Send([0x42], 1, new IPEndPoint(IPAddress.Broadcast, port));

            var remote = new IPEndPoint(IPAddress.Any, 0);
            listener.Receive(ref remote);
            return null;
        }
        catch (SocketException ex)
        {
            return $"本机不能把广播投递到本机的监听端口（{ex.SocketErrorCode}），跳过广播测试。";
        }
    }
}
