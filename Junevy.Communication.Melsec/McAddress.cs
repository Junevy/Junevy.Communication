using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 的软元件地址。在 <see cref="PlcAddress"/> 的基础上，<see cref="Device"/> 返回具体的 <see cref="McDevice"/>（含 ASCII 与二进制代码）。
/// 由软元件表的解析器创建。
/// </summary>
public sealed class McAddress : PlcAddress
{
    private readonly McDevice device;

    internal McAddress(McDevice device, int number, int? bitIndex, string text)
        : base(text, device, number, bitIndex)
    {
        this.device = device;
    }

    /// <summary>软元件定义（具体的 MELSEC 类型）。</summary>
    public new McDevice Device => device;
}
