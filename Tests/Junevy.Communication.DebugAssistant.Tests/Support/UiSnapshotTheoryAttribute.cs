using Xunit;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 界面快照测试：把每个页签渲染成 PNG，供审阅布局。未设置环境变量 <c>JUNEVY_UI_SNAPSHOT_DIR</c> 时显示为 Skipped。
/// </summary>
public sealed class UiSnapshotTheoryAttribute : TheoryAttribute
{
    /// <summary>环境变量名：值为输出目录。</summary>
    public const string VariableName = "JUNEVY_UI_SNAPSHOT_DIR";

    /// <summary>创建特性；未设置环境变量时跳过该测试。</summary>
    public UiSnapshotTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
            Skip = $"Set {VariableName} to a folder to render the pages as PNG files.";
    }
}
