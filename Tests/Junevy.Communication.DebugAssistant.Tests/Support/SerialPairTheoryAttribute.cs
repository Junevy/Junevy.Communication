using Junevy.Communication.Testing;
using Xunit;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 需要真实串口对的理论测试，与库的 <c>SerialPairFactAttribute</c> 使用相同的跳过规则：未设置 <c>JUNEVY_SERIAL_PAIR</c> 时显示为 Skipped。
/// </summary>
public sealed class SerialPairTheoryAttribute : TheoryAttribute
{
    /// <summary>创建特性；未设置环境变量时跳过该测试。</summary>
    public SerialPairTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SerialPairEnvironment.VariableName)))
            Skip = $"Set {SerialPairEnvironment.VariableName}=COMx,COMy (two ports wired to each other) to run the real serial port tests.";
    }
}
