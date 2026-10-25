using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Tests.Views;

/// <summary>
/// 界面快照：用真实的数据模板把每个页签渲染成 PNG（主题与应用资源与运行时相同），用于审阅布局。
/// 设置环境变量 <c>JUNEVY_UI_SNAPSHOT_DIR</c> 后运行；未设置时跳过。
/// </summary>
public sealed class UiSnapshotTests
{
    private const int Width = 1000;
    private const int Height = 720;

    public static IEnumerable<object[]> Kinds() => ProtocolCatalog.All.Select(descriptor => new object[] { descriptor.Kind });

    /// <summary>整窗截图：默认窗口大小（1280×800）下，协议树、页签条与打开的页签放在一起的效果。</summary>
    [UiSnapshotTheory]
    [InlineData(ProtocolKind.Modbus)]
    [InlineData(ProtocolKind.TcpClient)]
    [InlineData(ProtocolKind.Mqtt)]
    public async Task RenderMainWindow(ProtocolKind kind)
    {
        string directory = Environment.GetEnvironmentVariable(UiSnapshotTheoryAttribute.VariableName)!;
        Directory.CreateDirectory(directory);
        var (services, _) = TestShell.Create();

        UiThread.Invoke(() =>
        {
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            ProtocolTreeItem leaf = viewModel.Tree.SelectMany(group => group.Children).OfType<ProtocolTreeItem>()
                .First(item => item.Descriptor?.Kind == kind);
            viewModel.OpenSessionCommand.Execute(leaf);
            var window = new MainWindow { DataContext = viewModel };
            var root = (FrameworkElement)window.Content;
            window.Content = null;
            var frame = new Border
            {
                Width = 1264,
                Height = 761,
                Background = (Brush)Application.Current.FindResource("Theme.Brush.Background.App"),
                DataContext = viewModel,
                Child = root,
            };
            frame.Measure(new Size(frame.Width, frame.Height));
            frame.Arrange(new Rect(0, 0, frame.Width, frame.Height));
            frame.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)frame.Width, (int)frame.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "MainWindow-" + kind + ".png"));
            encoder.Save(stream);
        });

        await services.DisposeAsync();
    }

    [UiSnapshotTheory]
    [MemberData(nameof(Kinds))]
    public async Task RenderPage(ProtocolKind kind)
    {
        string directory = Environment.GetEnvironmentVariable(UiSnapshotTheoryAttribute.VariableName)!;
        Directory.CreateDirectory(directory);
        await using SessionViewModel page = TestShell.CreateSession(kind);

        UiThread.Invoke(() =>
        {
            var template = (DataTemplate)Application.Current.FindResource(new DataTemplateKey(page.GetType()));
            var content = (FrameworkElement)template.LoadContent();
            content.DataContext = page;
            var frame = new Border
            {
                Width = Width,
                Height = Height,
                Background = (Brush)Application.Current.FindResource("Theme.Brush.Background.App"),
                Child = content,
            };
            // 按位图大小排列：VisualTree.Layout 的 1000×800 会让 720 高的边框上下居中，截图顶部空出 40 像素、底部被截掉。
            frame.Measure(new Size(Width, Height));
            frame.Arrange(new Rect(0, 0, Width, Height));
            frame.UpdateLayout();

            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, kind + ".png"));
            encoder.Save(stream);
        });
    }
}
