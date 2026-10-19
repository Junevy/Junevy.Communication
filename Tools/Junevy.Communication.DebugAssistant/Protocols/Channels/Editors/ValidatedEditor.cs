using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>
/// 编辑器的基类：每次属性变化后，按该属性上的校验特性（<c>[Range]</c> 等）重新校验（计划 U7）。
/// 错误消息显示在字段旁边；<see cref="ObservableValidator.HasErrors"/> 为 true 时不生成库的选项。
/// </summary>
public abstract class ValidatedEditor : ObservableValidator
{
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { Length: > 0 } name && GetType().GetProperty(name) is { } property)
        {
            ValidateProperty(property.GetValue(this), name);
        }
    }
}
