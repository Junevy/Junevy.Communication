using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 客户端选项（P4-D19）：客户端构造时校验并取快照，之后修改选项对象不影响已创建的客户端，选项本身也不被改写。
/// S7 的设备内存顺序就是套件的规范化布局，因此默认字序为 <see cref="WordOrder.ABCD"/>，字符串编码默认 ASCII。
/// </summary>
public sealed class S7ClientOptions : PlcClientOptions
{
    /// <summary>Rack 号，0–7。默认 0。</summary>
    public int Rack { get; set; } = 0;

    /// <summary>槽号，0–31。默认 1（S7-300/400 的 CPU 在槽 1，S7-1500 与 LOGO 常用 0）。</summary>
    public int Slot { get; set; } = 1;

    /// <summary>连接类型。默认 <see cref="S7ConnectionType.PG"/>。</summary>
    public S7ConnectionType ConnectionType { get; set; } = S7ConnectionType.PG;

    /// <summary>字序与字符串编码；为 null 时使用 ABCD 字序与 ASCII 字符串（P4-D19）。</summary>
    public PlcDataFormat? DataFormat { get; set; }
}
