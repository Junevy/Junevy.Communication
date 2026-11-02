namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 与 Modbus Slave 软件（mbslave.exe，由人预先启动）核对的测试。只有设置了对应的环境变量时才执行，未设置时显示为 Skipped。
/// 从站要求：站号 1，保持寄存器窗口至少覆盖 HR0–HR9（测试会改写并在结束时还原）。
/// </summary>
public sealed class ModbusToolFactAttribute : FactAttribute
{
    /// <summary>环境变量名：Modbus TCP 从站的地址，如 <c>127.0.0.1:502</c>。</summary>
    public const string TcpVariable = "JUNEVY_MODBUS_TOOL_TCP";

    /// <summary>环境变量名：客户端使用的串口，如 Modbus Slave 占用 COM20 时填虚拟对的另一端 <c>COM21</c>。</summary>
    public const string SerialVariable = "JUNEVY_MODBUS_TOOL_SERIAL";

    /// <summary>创建特性；未设置环境变量时跳过该测试。</summary>
    /// <param name="variable">测试需要的环境变量名。</param>
    public ModbusToolFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            Skip = $"Set {variable} and start Modbus Slave (station 1, holding registers HR0-HR9) to run the tool interop tests.";
    }
}
