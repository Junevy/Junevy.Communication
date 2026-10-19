using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 传输页测试的公共部分：回环地址上的库 <see cref="TcpServer"/>（端口用 <see cref="FreePort"/> 取得，因为它不接受 0），
/// 以及 UDP 对端与页签的创建。页签的界面派发是 <see cref="ManualUiDispatcher"/>，由测试泵送。
/// </summary>
internal static class ChannelTestKit
{
    /// <summary>启动回环地址上的 TCP 服务端，每收到一帧调用 <paramref name="onFrame"/>。返回的服务端由调用方释放。</summary>
    public static async Task<TcpServer> StartServerAsync(FramingOptions framing, Func<ITcpSession, byte[], Task> onFrame)
    {
        var server = new TcpServer(new TcpServerConfig { ListenAddress = "127.0.0.1", Port = FreePort.Get(), Framing = framing });
        server.FrameReceived += async (_, e) => await onFrame(e.Session, e.Data);
        CommResult started = await server.StartAsync();
        if (!started.IsSuccess)
        {
            await server.DisposeAsync();
            throw new InvalidOperationException(started.ErrorMessage);
        }

        return server;
    }

    /// <summary>与页签使用相同映射的分帧选项（服务端与客户端必须按同一种分帧收发）。</summary>
    public static FramingOptions FixedLength(int length) => new FramingEditor { Mode = FramingMode.FixedLength, FixedLength = length }.ToOptions();

    public static TcpClientSessionViewModel TcpPage(ManualUiDispatcher dispatcher, int port)
    {
        var page = new TcpClientSessionViewModel(ProtocolCatalog.Get(ProtocolKind.TcpClient), FakeServices.Create(dispatcher));
        page.Host = "127.0.0.1";
        page.Port = port;
        return page;
    }

    /// <summary>TCP 服务端页签，监听回环地址上的 <paramref name="port"/>（由测试用 <see cref="FreePort"/> 取得）。</summary>
    public static TcpServerSessionViewModel TcpServerPage(ManualUiDispatcher dispatcher, int port)
    {
        var page = new TcpServerSessionViewModel(ProtocolCatalog.Get(ProtocolKind.TcpServer), FakeServices.Create(dispatcher));
        page.Settings.ListenAddress = "127.0.0.1";
        page.Settings.Port = port;
        return page;
    }

    public static UdpSessionViewModel UdpPage(ManualUiDispatcher dispatcher) =>
        new(ProtocolCatalog.Get(ProtocolKind.Udp), FakeServices.Create(dispatcher));

    public static SerialSessionViewModel SerialPage(ManualUiDispatcher dispatcher) =>
        new(ProtocolCatalog.Get(ProtocolKind.Serial), FakeServices.Create(dispatcher));

    /// <summary>回环地址上一个 UDP 对端，端口由系统选择。</summary>
    public static UdpClient UdpPeer() => new(new IPEndPoint(IPAddress.Loopback, 0));

    public static int PortOf(UdpClient client) => ((IPEndPoint)client.Client.LocalEndPoint!).Port;
}
