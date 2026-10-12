using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 的存储区地址：<see cref="PlcAddress.Number"/> 是字号，<see cref="PlcAddress.BitIndex"/> 是字内的位号 0–15
/// （没有位号时为 null，表示按字访问）。由软元件表的解析器创建。
/// 软元件只保存在基类中，<see cref="Device"/> 做类型转换，不另存副本。
/// </summary>
public sealed class FinsAddress : PlcAddress
{
    internal FinsAddress(FinsDevice device, int number, int? bitIndex, string text)
        : base(text, device, number, bitIndex)
    {
    }

    /// <summary>软元件定义（具体的 FINS 类型，含字区与位区代码）。</summary>
    public new FinsDevice Device => (FinsDevice)base.Device;
}
