using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 与第三方从站 pymodbus 互通的测试。只有设置了环境变量 <c>JUNEVY_PYMODBUS_PYTHON</c>（装有 <c>pymodbus[serial]</c> 的 python.exe 路径）时才执行；
/// <see cref="NeedsSerialPair"/> 为 true 时还需要 <c>JUNEVY_SERIAL_PAIR</c>。未设置时测试显示为 Skipped。
/// </summary>
public sealed class PymodbusFactAttribute : FactAttribute
{
    /// <summary>环境变量名：python.exe 的完整路径。</summary>
    public const string VariableName = "JUNEVY_PYMODBUS_PYTHON";

    private bool _needsSerialPair;

    /// <summary>创建特性；未设置环境变量时跳过该测试。</summary>
    public PymodbusFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VariableName)))
            Skip = $"Set {VariableName} to a python.exe with pymodbus[serial] installed to run the third-party interop tests.";
    }

    /// <summary>为 true 时还需要真实串口对（<c>JUNEVY_SERIAL_PAIR</c>）。</summary>
    public bool NeedsSerialPair
    {
        get => _needsSerialPair;
        set
        {
            _needsSerialPair = value;
            if (value && Skip is null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SerialPairEnvironment.VariableName)))
                Skip = $"Set {SerialPairEnvironment.VariableName}=COMx,COMy (two ports wired to each other) to run the serial interop tests.";
        }
    }
}
