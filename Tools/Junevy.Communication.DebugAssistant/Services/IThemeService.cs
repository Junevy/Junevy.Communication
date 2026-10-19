namespace Junevy.Communication.DebugAssistant.Services;

/// <summary>在浅色与深色主题之间切换（WPF 实现位于 Views/Platform）。</summary>
public interface IThemeService
{
    /// <summary>切换到另一种主题。</summary>
    void Toggle();
}
