using System.Net;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 事件参数的公开构造函数保存全部参数；<see cref="FrameReceivedEventArgs"/> 拒绝 null 数据（计划 Task 4）。
/// </summary>
public sealed class ChannelEventArgsTests
{
    [Fact]
    public void ConnectionStateChangedEventArgs_KeepsEveryConstructorArgument()
    {
        var exception = new IOException("link down");

        var args = new ConnectionStateChangedEventArgs(ConnectionState.Connected, ConnectionState.Reconnecting,
            DisconnectReason.RemoteClosed, exception, 3);

        Assert.Equal(ConnectionState.Connected, args.PreviousState);
        Assert.Equal(ConnectionState.Reconnecting, args.CurrentState);
        Assert.Equal(DisconnectReason.RemoteClosed, args.Reason);
        Assert.Same(exception, args.Exception);
        Assert.Equal(3, args.ReconnectAttempt);
    }

    [Fact]
    public void FrameReceivedEventArgs_KeepsEveryConstructorArgument()
    {
        byte[] data = { 1, 2, 3 };
        var receivedAt = new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);
        var remote = new IPEndPoint(IPAddress.Loopback, 502);

        var args = new FrameReceivedEventArgs(data, receivedAt, remote);

        Assert.Same(data, args.Data);
        Assert.Equal(receivedAt, args.ReceivedAt);
        Assert.Same(remote, args.RemoteEndPoint);
    }

    [Fact]
    public void FrameReceivedEventArgs_RejectsNullData()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new FrameReceivedEventArgs(null!, DateTimeOffset.UtcNow, null));

        Assert.Equal("data", exception.ParamName);
    }
}
