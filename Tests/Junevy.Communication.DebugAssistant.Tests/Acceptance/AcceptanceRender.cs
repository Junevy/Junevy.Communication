using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Acceptance;

/// <summary>验收走查的截图：用应用的数据模板渲染页签，或渲染整个主窗口的内容（与 1280×800 窗口的客户区同大）。</summary>
internal static class AcceptanceRender
{
    public const int PageWidth = 1000;
    public const int PageHeight = 720;
    public const int WindowWidth = 1264;
    public const int WindowHeight = 761;

    public static string Directory
    {
        get
        {
            string directory = Environment.GetEnvironmentVariable(AcceptanceFactAttribute.VariableName)!;
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>渲染一个页签；<paramref name="tab"/> 不为空时先选中底部活动区里同名的页签。</summary>
    public static void Page(SessionViewModel page, string fileName, string? tab = "操作记录") => UiThread.Invoke(() =>
    {
        var template = (DataTemplate)Application.Current.FindResource(new DataTemplateKey(page.GetType()));
        var content = (FrameworkElement)template.LoadContent();
        content.DataContext = page;
        Save(content, page, PageWidth, PageHeight, fileName, tab);
    });

    /// <summary>渲染主窗口的内容（工具栏、协议树与页签条）。</summary>
    public static void Window(FrameworkElement root, object dataContext, string fileName, string? tab = null) =>
        UiThread.Invoke(() => Save(root, dataContext, WindowWidth, WindowHeight, fileName, tab));

    private static void Save(FrameworkElement content, object dataContext, int width, int height, string fileName, string? tab)
    {
        var frame = new Border
        {
            Width = width,
            Height = height,
            Background = (Brush)Application.Current.FindResource("Theme.Brush.Background.App"),
            DataContext = dataContext,
            Child = content,
        };
        Arrange(frame);
        if (tab is not null && FindTab(frame, tab) is { } item)
        {
            item.IsSelected = true;
            Arrange(frame);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Directory, fileName));
        encoder.Save(stream);
        frame.Child = null;
    }

    private static void Arrange(FrameworkElement frame)
    {
        frame.Measure(new Size(frame.Width, frame.Height));
        frame.Arrange(new Rect(0, 0, frame.Width, frame.Height));
        frame.UpdateLayout();
    }

    private static TabItem? FindTab(DependencyObject root, string header)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TabItem item && Equals(item.Header, header))
            {
                return item;
            }

            if (child is DependencyObject node && FindTab(node, header) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
