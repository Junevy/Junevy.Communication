namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 需要真实串口对的测试。只有设置了环境变量 <c>JUNEVY_SERIAL_PAIR</c>（形如 <c>COM5,COM6</c>）时才执行；未设置时在构造期间设置 <see cref="FactAttribute.Skip"/>，测试显示为 Skipped。
/// </summary>
public sealed class SerialPairFactAttribute : FactAttribute
{
    /// <summary>创建特性；未设置环境变量时跳过该测试。</summary>
    public SerialPairFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SerialPairEnvironment.VariableName)))
            Skip = $"Set {SerialPairEnvironment.VariableName}=COMx,COMy (two ports wired to each other) to run the real serial port tests.";
    }
}
