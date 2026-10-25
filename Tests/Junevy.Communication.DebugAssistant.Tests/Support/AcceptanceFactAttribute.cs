using Xunit;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 验收走查（计划第 31 节第 3 条）：真实的页签视图模型经回环套接字与模拟设备、服务端页签互连，逐项断言并把界面渲染成 PNG。
/// 未设置环境变量 <c>JUNEVY_ACCEPTANCE_DIR</c> 时显示为 Skipped。
/// </summary>
public sealed class AcceptanceFactAttribute : FactAttribute
{
    /// <summary>环境变量名：值为截图的输出目录。</summary>
    public const string VariableName = "JUNEVY_ACCEPTANCE_DIR";

    /// <summary>创建特性；未设置环境变量时跳过该测试。</summary>
    public AcceptanceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
            Skip = $"Set {VariableName} to a folder to run the acceptance walkthrough.";
    }
}
