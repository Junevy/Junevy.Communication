using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 需要"发往不可路由地址的 SYN 被丢弃"这一网络环境的测试（连接超时，计划 10.3）。
/// 构造期间用原始套接字以 500 ms 时限连接 10.255.255.1:9：若在 300 ms 之内就以非超时错误结束（或意外连通），
/// 说明本机立即拒绝这类连接（例如没有可用路由），无法测量连接超时，此时设置 <see cref="FactAttribute.Skip"/>。
/// 只在测试发现时探测一次；探测为黑洞（500 ms 内没有结束，或超时错误）时测试照常执行。
/// </summary>
public sealed class BlackholeAddressFactAttribute : FactAttribute
{
    private const string Target = "10.255.255.1";
    private const int Port = 9;
    private const int TimeLimitMilliseconds = 500;
    private const int ImmediateMilliseconds = 300;

    /// <summary>创建特性；本机立即拒绝不可路由地址的连接时跳过该测试。</summary>
    public BlackholeAddressFactAttribute()
    {
        string? reason = ProbeBlackhole();
        if (reason != null)
            Skip = reason;
    }

    private static string? ProbeBlackhole()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var watch = Stopwatch.StartNew();
        try
        {
            IAsyncResult pending = socket.BeginConnect(new IPEndPoint(IPAddress.Parse(Target), Port), null, null);
            if (!pending.AsyncWaitHandle.WaitOne(TimeLimitMilliseconds))
                return null; // 500 ms 内没有结束：SYN 被丢弃，连接超时可以测量。

            long elapsed = watch.ElapsedMilliseconds;
            socket.EndConnect(pending);
            return elapsed < ImmediateMilliseconds
                ? $"This machine accepted a connection to {Target}:{Port} immediately; connect timeouts cannot be measured here."
                : null;
        }
        catch (SocketException ex) when (ex.SocketErrorCode != SocketError.TimedOut && watch.ElapsedMilliseconds < ImmediateMilliseconds)
        {
            return $"This machine refuses connections to {Target} immediately ({ex.SocketErrorCode}); connect timeouts cannot be measured here.";
        }
        catch (SocketException)
        {
            return null; // 超时类错误：超时路径可以执行。
        }
    }
}
