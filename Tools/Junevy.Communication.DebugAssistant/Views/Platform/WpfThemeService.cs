using Junevy.Communication.DebugAssistant.Services;
using Junevy.Controls.Themes;

namespace Junevy.Communication.DebugAssistant.Views.Platform;

/// <summary>基于 Junevy.Controls 的主题管理器实现 <see cref="IThemeService"/>。</summary>
internal sealed class WpfThemeService : IThemeService
{
    public void Toggle() => ThemeManager.ToggleTheme();
}
