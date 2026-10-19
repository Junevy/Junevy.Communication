using System.Windows;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;
using JvTabControl = Junevy.Controls.Controls.Menu.TabControl;
using JvTabCloseEventArgs = Junevy.Controls.Controls.Menu.TabCloseEventArgs;

namespace Junevy.Communication.DebugAssistant.Views.Behaviors;

/// <summary>
/// 把页签的关闭请求交给命令。控件自己的移除被取消（<c>Cancel = true</c>），页签在命令释放完会话后才从集合中移除。
/// </summary>
public sealed class CloseTabToCommandBehavior : Behavior<JvTabControl>
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(CloseTabToCommandBehavior));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.TabClosing += OnTabClosing;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.TabClosing -= OnTabClosing;
        base.OnDetaching();
    }

    private void OnTabClosing(object? sender, JvTabCloseEventArgs e)
    {
        e.Cancel = true;
        if (Command is { } command && command.CanExecute(e.Item))
        {
            command.Execute(e.Item);
        }
    }
}
