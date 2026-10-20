using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;

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
            VisualTree.Layout(frame);

            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(frame);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, kind + ".png"));
            encoder.Save(stream);
        });
    }
}
