namespace Junevy.Communication.Plc;

/// <summary>
/// 地址的公共部分：原始文本、软元件、编号与位号。协议派生出自己的地址类型（例如 MELSEC 的 <c>McAddress</c>，增加软元件代码）。
/// 实例由 <see cref="PlcAddressParser{TDevice, TAddress}"/> 通过工厂函数创建。
/// </summary>
public abstract class PlcAddress
{
    /// <summary>
    /// 初始化地址。参数的合法性（编号范围、位号范围等）由 <see cref="PlcAddressParser{TDevice, TAddress}"/> 校验。
    /// </summary>
    /// <param name="text">去掉首尾空白后的原始文本。</param>
    /// <param name="device">软元件定义。</param>
    /// <param name="number">编号（已按软元件进制解析）。</param>
    /// <param name="bitIndex">位号（字软元件 0–15，字节软元件 0–7，位软元件不带）；没有位号时为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 或 <paramref name="device"/> 为 null。</exception>
    protected PlcAddress(string text, IPlcDeviceDefinition device, int number, int? bitIndex)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Number = number;
        BitIndex = bitIndex;
    }

    /// <summary>去掉首尾空白后的原始文本。</summary>
    public string Text { get; }

    /// <summary>软元件定义。</summary>
    public IPlcDeviceDefinition Device { get; }

    /// <summary>访问单位，等于 <see cref="IPlcDeviceDefinition.Unit"/>。</summary>
    public PlcAddressUnit Unit => Device.Unit;

    /// <summary>编号。</summary>
    public int Number { get; }

    /// <summary>位号（字软元件 0–15，字节软元件 0–7），不出现在位软元件上；没有位号时为 null。</summary>
    public int? BitIndex { get; }

    /// <summary>返回原始文本。</summary>
    /// <returns>去掉首尾空白后的原始文本。</returns>
    public override string ToString() => Text;
}
