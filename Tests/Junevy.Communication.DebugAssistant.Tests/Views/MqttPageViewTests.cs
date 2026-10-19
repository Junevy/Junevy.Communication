using Junevy.Communication.DebugAssistant.Protocols.Mqtt;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Mqtt;
using Junevy.Communication.DebugAssistant.Views.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>MQTT 客户端页的 XAML（计划 Task 17）：视图加载并绑定到页签，日志面板绑定到页签的日志。</summary>
public sealed class MqttPageViewTests
{
    [Fact]
    public async Task MqttPageView_LoadsAndBindsItsSession()
    {
        await using var page = new MqttSessionViewModel(
            ProtocolCatalog.Get(ProtocolKind.Mqtt),
            FakeServices.Create(new ManualUiDispatcher()));

        UiThread.Invoke(() =>
        {
            var view = new MqttPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }
}
