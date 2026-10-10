namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 真实串口对的环境变量约定：<c>JUNEVY_SERIAL_PAIR=COM5,COM6</c>（两个端口需以 null-modem 线缆直连，或由 com0com 等虚拟串口对互连）。
/// 未设置时真实端口测试被跳过；本机存在的 COM 端口可能连接着实际设备，因此默认测试从不打开真实端口。
/// </summary>
internal static class SerialPairEnvironment
{
    /// <summary>环境变量名。</summary>
    public const string VariableName = "JUNEVY_SERIAL_PAIR";

    /// <summary>读取两个端口名。格式错误或两个名称相同时抛出 <see cref="InvalidOperationException"/>（测试失败，而不是静默跳过）。</summary>
    /// <returns>两个端口名（去除首尾空白）。</returns>
    public static (string First, string Second) Read()
    {
        string[] parts = (Environment.GetEnvironmentVariable(VariableName) ?? string.Empty).Split(',');
        if (parts.Length != 2)
            throw new InvalidOperationException($"{VariableName} must name exactly two ports, for example COM5,COM6.");

        string first = parts[0].Trim();
        string second = parts[1].Trim();
        if (first.Length == 0 || second.Length == 0 || string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{VariableName} must name two different ports, for example COM5,COM6.");

        return (first, second);
    }
}
