using System.Windows;

namespace Junevy.Communication.DebugAssistant.Views.Shared;

/// <summary>
/// 绑定代理：DataGrid 的列不在视觉树与逻辑树中，拿不到 DataContext，列上的 <c>{Binding}</c> 会静默失败。
/// 把代理放进资源（Freezable 会继承所在元素的 DataContext），列再以 <c>{Binding Data.X, Source={StaticResource Proxy}}</c> 绑定。
/// </summary>
public sealed class BindingProxy : Freezable
{
    /// <summary>代理的数据，通常绑定为 <c>{Binding}</c>。</summary>
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    /// <summary>代理的数据。</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
