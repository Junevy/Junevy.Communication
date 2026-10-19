using System.Windows.Controls;
using Junevy.Communication.DebugAssistant.Protocols.OpcUa;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.OpcUa;
using Junevy.Communication.DebugAssistant.Views.Sessions;
using Junevy.Communication.OpcUa;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>OPC UA 客户端页的 XAML（计划 Task 17）：视图加载并绑定到页签；浏览的根节点显示在树中，并带有占位子节点。</summary>
public sealed class OpcUaPageViewTests
{
    private static OpcUaSessionViewModel CreatePage() =>
        new(ProtocolCatalog.Get(ProtocolKind.OpcUa), FakeServices.Create(new ManualUiDispatcher()));

    [Fact]
    public async Task OpcUaPageView_LoadsAndBindsItsSession()
    {
        await using var page = CreatePage();

        UiThread.Invoke(() =>
        {
            var view = new OpcUaPageView { DataContext = page };
            VisualTree.Layout(view);

            Assert.Same(page, view.DataContext);
            LogPanel? panel = VisualTree.FindChild<LogPanel>(view);
            Assert.NotNull(panel);
            Assert.Same(page.LogPanel, panel.DataContext);
        });
    }

    [Fact]
    public async Task BrowseTree_ShowsTheRoot()
    {
        await using var page = CreatePage();
        page.BrowseRoots.Add(new OpcUaNodeItem("i=85", "Objects", "Objects", OpcUaNodeClass.Object, _ => Task.CompletedTask));

        UiThread.Invoke(() =>
        {
            var view = new OpcUaPageView { DataContext = page };
            VisualTree.Layout(view);

            TreeView? tree = VisualTree.FindChild<TreeView>(view);
            Assert.NotNull(tree);
            Assert.Single(tree.Items);
        });
    }
}
