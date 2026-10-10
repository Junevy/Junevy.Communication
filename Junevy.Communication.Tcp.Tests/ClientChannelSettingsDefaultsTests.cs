using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 公开基类 <see cref="ClientChannelSettings"/> 的超时默认值与 TCP 客户端配置一致。
/// 原默认值 0 表示不限时，新传输作者容易写出"请求永不超时"的通道；对齐后，遗漏赋值的通道仍有超时保护。
/// </summary>
public sealed class ClientChannelSettingsDefaultsTests
{
    [Fact]
    public void ClientChannelSettings_DefaultsMatchTcpClient()
    {
        var tcp = new TcpClientChannelConfig();
        var settings = new ClientChannelSettings();

        Assert.Equal(tcp.HandshakeTimeout, settings.HandshakeTimeout);
        Assert.Equal(tcp.SendTimeout, settings.SendTimeout);
        Assert.Equal(tcp.RequestTimeout, settings.RequestTimeout);
        Assert.Equal(tcp.DisconnectTimeout, settings.DisconnectTimeout);
        Assert.Equal(tcp.LateReplyWindow, settings.LateReplyWindow);
        Assert.Equal(tcp.IdleTimeout, settings.IdleTimeout);
        Assert.Equal(tcp.PartialFrameTimeout, settings.PartialFrameTimeout);
        Assert.Equal(tcp.Correlation, settings.Correlation);
        Assert.Equal(tcp.ReceiveQueueCapacity, settings.ReceiveQueueCapacity);
        Assert.Equal(tcp.QueueFullMode, settings.QueueFullMode);
        Assert.Equal(4096, settings.ReceiveBufferSize);

        // ResetOnRequestTimeout 不对齐（TCP 配置默认 true，基类默认 false）：派生传输应显式选择是否重建连接。
        Assert.False(settings.ResetOnRequestTimeout);
    }
}
