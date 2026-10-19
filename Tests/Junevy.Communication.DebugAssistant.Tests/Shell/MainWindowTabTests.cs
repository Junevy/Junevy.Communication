using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using JvTabControl = Junevy.Controls.Controls.Menu.TabControl;
using JvTabControlItem = Junevy.Controls.Controls.Menu.TabControlItem;
using JvTreeItemEventArgs = Junevy.Controls.Controls.Menu.TreeItemEventArgs;
using JvTreeView = Junevy.Controls.Controls.Menu.TreeView;

namespace Junevy.Communication.DebugAssistant.Tests.Shell;

/// <summary>在真实的主窗口与 <c>jv:TabControl</c> 上验证改名与关闭（计划 Task 1 第 5 条）。</summary>
public sealed class MainWindowTabTests
{
    [Fact]
    public void Header_FollowsTheTitleOfTheViewModel()
    {
        UiThread.Invoke(() =>
        {
            using TabHarness harness = TabHarness.OpenModbusTab();

            Assert.Equal("Modbus #1", harness.Item.Header);

            harness.Session.Title = "产线 A";

            Assert.Equal("产线 A", harness.Item.Header);
        });
    }

    [Fact]
    public void Rename_ByDoubleClickAndEnter_UpdatesTheTitleOfTheViewModel()
    {
        UiThread.Invoke(() =>
        {
            using TabHarness harness = TabHarness.OpenModbusTab();
            JvTabControlItem item = harness.Item;

            DoubleClick(item);
            Assert.True(item.IsEditing);

            var editor = (TextBox)item.Template.FindName("PART_EditHeaderTextBox", item);
            editor.Text = "产线 B";
            Commit(editor);

            Assert.False(item.IsEditing);
            Assert.Equal("产线 B", harness.Session.Title);
        });
    }

    [Fact]
    public void DoubleClickOnTheTree_OpensALeafSession_AndIgnoresAGroup()
    {
        UiThread.Invoke(() =>
        {
            using TabHarness harness = TabHarness.OpenModbusTab();
            var tree = (JvTreeView)harness.Window.FindName("ProtocolTree");
            var group = (ProtocolTreeItem)harness.ViewModel.Tree[0];
            ProtocolTreeItem leaf = (ProtocolTreeItem)group.Children[0];
            tree.UpdateLayout();
            var groupContainer = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(group);
            groupContainer.UpdateLayout();
            var leafContainer = (TreeViewItem)groupContainer.ItemContainerGenerator.ContainerFromItem(leaf);

            tree.RaiseEvent(new JvTreeItemEventArgs(JvTreeView.ItemDoubleClickEvent, tree, leafContainer, leaf));

            Assert.Equal(["Modbus #1", "Modbus #2"], harness.ViewModel.Sessions.Select(s => s.Title));

            tree.RaiseEvent(new JvTreeItemEventArgs(JvTreeView.ItemDoubleClickEvent, tree, groupContainer, group));

            Assert.Equal(2, harness.ViewModel.Sessions.Count);
        });
    }

    [Fact]
    public void Rename_ByF2_UpdatesTheTitleOfTheViewModel()
    {
        UiThread.Invoke(() =>
        {
            using TabHarness harness = TabHarness.OpenModbusTab();
            JvTabControlItem item = harness.Item;

            item.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(harness.Window), 0, Key.F2)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
            Assert.True(item.IsEditing);

            var editor = (TextBox)item.Template.FindName("PART_EditHeaderTextBox", item);
            editor.Text = "产线 C";
            Commit(editor);

            Assert.False(item.IsEditing);
            Assert.Equal("产线 C", harness.Session.Title);
        });
    }

    [Fact]
    public void CloseTab_DisposesTheSessionAndRemovesTheTab()
    {
        UiThread.Invoke(() =>
        {
            List<TrackingSession> created = [];
            using TabHarness harness = TabHarness.OpenModbusTab(s => created = TestShell.TrackKind(s, ProtocolKind.Modbus));
            TrackingSession session = Assert.Single(created);
            session.IsListed = () => harness.ViewModel.Sessions.Contains(session);

            harness.Tabs.CloseTab(harness.Item);

            TrackingSession tracked = Assert.Single(created);
            Assert.Equal(1, tracked.DisposeCount);
            Assert.True(tracked.ListedWhenDisposed);
            Assert.Empty(harness.ViewModel.Sessions);
        });
    }

    /// <summary>
    /// 模拟双击页签头。<c>RaiseEvent(MouseDoubleClick)</c> 在测试里不会派发到 <c>OnMouseDoubleClick</c>，
    /// 因此直接调用页签容器自己的重写（它只检查事件来源与闸门，不依赖真实的鼠标状态）。
    /// </summary>
    private static void DoubleClick(JvTabControlItem item)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = item,
        };
        MethodInfo onDoubleClick = typeof(JvTabControlItem).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        onDoubleClick.Invoke(item, new object[] { args });
    }

    /// <summary>
    /// 提交改名。库在编辑框失去焦点（LostFocus）时把文本经绑定写回 Header；Enter 与 Escape 只是把焦点移出。
    /// 测试窗口未必处于活动状态，因此直接触发 LostFocus，验证的是提交路径本身。
    /// </summary>
    private static void Commit(TextBox editor) => editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, editor));

    /// <summary>显示主窗口并打开一个 Modbus 页签；释放时关闭窗口。只能在 UI 线程上使用。</summary>
    private sealed class TabHarness : IDisposable
    {
        private TabHarness(MainWindow window, JvTabControl tabs, MainWindowViewModel viewModel, SessionViewModel session)
        {
            Window = window;
            Tabs = tabs;
            ViewModel = viewModel;
            Session = session;
        }

        public MainWindow Window { get; }

        public JvTabControl Tabs { get; }

        public MainWindowViewModel ViewModel { get; }

        public SessionViewModel Session { get; }

        /// <summary>页签的容器；每次读取都重新查询，容器可能被重新生成。</summary>
        public JvTabControlItem Item => (JvTabControlItem)Tabs.ItemContainerGenerator.ContainerFromItem(Session);

        public static TabHarness OpenModbusTab(Action<IServiceCollection>? overrides = null)
        {
            var (services, _) = TestShell.Create(overrides);
            MainWindowViewModel viewModel = services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = viewModel };
            window.Show();
            var tabs = (JvTabControl)window.FindName("SessionTabs");

            ProtocolTreeItem leaf = viewModel.Tree.SelectMany(group => group.Children).OfType<ProtocolTreeItem>()
                .Single(item => item.Descriptor?.Kind == ProtocolKind.Modbus);
            viewModel.OpenSessionCommand.Execute(leaf);
            window.UpdateLayout();

            return new TabHarness(window, tabs, viewModel, viewModel.Sessions.Single());
        }

        public void Dispose() => Window.Close();
    }
}
