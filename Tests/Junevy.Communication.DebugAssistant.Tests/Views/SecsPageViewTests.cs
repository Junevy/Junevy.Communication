using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.Secs;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Secs;
using Junevy.Communication.DebugAssistant.Views.Sessions;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>
/// SECS/HSMS 设备端页的 XAML（计划 Task 15）：视图加载并绑定到页签；规则表随登记而显示；登记的规则出现在表中。
/// </summary>
public sealed class SecsPageViewTests
{
    private static SecsSessionViewModel CreatePage() =>
        new(ProtocolCatalog.Get(ProtocolKind.Secs), FakeServices.Create(new ManualUiDispatcher()));

    [Fact]
    public async Task SecsPageView_LoadsAndBindsItsSession()
    {
        await using var page = CreatePage();

        UiThread.Invoke(() =>
        {
            var view = new SecsPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }

    [Fact]
    public async Task RuleGrid_ShowsTheRuleAfterItIsRegistered()
    {
        await using var page = CreatePage();

        UiThread.Invoke(() =>
        {
            var view = new SecsPageView { DataContext = page };
            VisualTree.Layout(view);
            var grid = (DataGrid)view.FindName("RuleGrid")!;
            Assert.Empty(grid.Items);

            page.NewRuleStream = 1;
            page.NewRuleFunction = 1;
            page.NewRuleKind = SecsRuleKind.Abort;
            page.NewRuleSml = string.Empty;
            page.AddRuleCommand.Execute(null);

            Assert.Single(grid.Items);
        });
    }
}
