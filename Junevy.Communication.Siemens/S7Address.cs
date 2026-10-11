using Junevy.Communication.Plc;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 的地址：编号是字节号，<see cref="DbNumber"/> 是数据块号（非 DB 区为 0），位号为 0–7。
/// 由内部的地址解析器创建；<see cref="PlcAddress.Text"/> 保留去掉首尾空白后的原文，包括可选的 <c>%</c> 前缀。
/// </summary>
public sealed class S7Address : PlcAddress
{
    internal S7Address(S7Device device, int dbNumber, int byteNumber, int? bitIndex, string text)
        : base(text, device, byteNumber, bitIndex)
    {
        DbNumber = dbNumber;
    }

    /// <summary>存储区定义（具体的 S7 类型）。</summary>
    public new S7Device Device => (S7Device)base.Device;

    /// <summary>数据块号；I、Q、M 区为 0。</summary>
    public int DbNumber { get; }
}
