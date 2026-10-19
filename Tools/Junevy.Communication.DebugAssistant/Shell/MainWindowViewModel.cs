using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.DebugAssistant.Services;

namespace Junevy.Communication.DebugAssistant.Shell;

/// <summary>主窗口的视图模型。</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IThemeService _themes;

    public MainWindowViewModel(IThemeService themes)
    {
        _themes = themes;
    }

    [RelayCommand]
    private void ToggleTheme() => _themes.Toggle();
}
