using System.Windows.Controls;

namespace Junevy.Communication.DebugAssistant.Views.Channels;

/// <summary>通道参数编辑视图。数据与校验都在 <c>ChannelSettingsEditor</c> 中；代码后置只调用 <c>InitializeComponent</c>（U1）。</summary>
public partial class ChannelSettingsView : UserControl
{
    public ChannelSettingsView()
    {
        InitializeComponent();
    }
}
