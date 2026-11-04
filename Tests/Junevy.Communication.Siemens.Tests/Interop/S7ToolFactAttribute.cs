namespace Junevy.Communication.Siemens.Tests.Interop;

/// <summary>
/// 与第三方 S7 服务器软件（HslCommunicationDemo 的西门子虚拟服务器，由人预先启动）核对的测试。
/// 只有设置了环境变量时才执行，未设置时显示为 Skipped。
/// 服务器要求：DB1、DB2、M、I、Q 区可读写，DB1 至少 4096 字节、其余至少 64 字节（测试会改写并在结束时还原），DB100 不存在。
/// </summary>
public sealed class S7ToolFactAttribute : FactAttribute
{
    /// <summary>环境变量名：服务器地址，如 <c>127.0.0.1:102</c>。</summary>
    public const string Variable = "JUNEVY_S7_TOOL_TCP";

    /// <summary>创建特性；未设置环境变量时跳过该测试。</summary>
    public S7ToolFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} and start an S7 server (DB1, DB2, M, I and Q writable; DB100 absent) to run the tool interop tests.";
    }
}
