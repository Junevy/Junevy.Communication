using System.Windows.Media;
using Junevy.Communication.DebugAssistant.Hosting;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Views.Shell;
using Junevy.Controls.Themes;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Tests.Shell;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public void ToggleThemeCommand_ChangesTheWindowBackground()
    {
        UiThread.Invoke(() =>
        {
            ThemeManager.ApplyTheme(AppTheme.Light);
            try
            {
                var viewModel = new ServiceCollection()
                    .AddShellServices()
                    .BuildServiceProvider()
                    .GetRequiredService<MainWindowViewModel>();
                var window = new MainWindow { DataContext = viewModel };

                Color light = ((SolidColorBrush)window.Background).Color;
                viewModel.ToggleThemeCommand.Execute(null);
                Color dark = ((SolidColorBrush)window.Background).Color;

                Assert.Equal(AppTheme.Dark, ThemeManager.CurrentTheme);
                Assert.NotEqual(light, dark);
            }
            finally
            {
                ThemeManager.ApplyTheme(AppTheme.Light);
            }
        });
    }
}
