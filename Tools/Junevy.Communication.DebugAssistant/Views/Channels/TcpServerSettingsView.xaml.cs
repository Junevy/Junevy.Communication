using System.Windows.Controls;

namespace Junevy.Communication.DebugAssistant.Views.Channels;

/// <summary>TCP 服务端的会话参数（分帧、心跳、超时、套接字、握手、TLS）。</summary>
public partial class TcpServerSettingsView : UserControl
{
    public TcpServerSettingsView()
    {
        InitializeComponent();
    }
}
