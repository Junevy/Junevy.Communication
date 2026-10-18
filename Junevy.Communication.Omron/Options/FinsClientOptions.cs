using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 客户端选项（P4-D18）：客户端构造时校验并取快照，之后修改选项对象不影响已创建的客户端，选项本身也不被改写。
/// FINS 的存储区按字寻址、字内位号 0–15，因此地址一律是字软元件；默认数据格式为 CDAB 字序 + ASCII 字符串（P4-D11）。
/// </summary>
public sealed class FinsClientOptions : PlcClientOptions
{
    /// <summary>客户端节点号：0 表示由 PLC 在节点地址握手时分配，1–254 为固定节点号；255 是 FINS 的广播地址，被拒绝。默认 0。</summary>
    public byte ClientNode { get; set; } = 0;

    /// <summary>目的网络号 DNA，写入每条请求的头。默认 0（与 PLC 同一网络）。</summary>
    public byte DestinationNetwork { get; set; } = 0;

    /// <summary>目的单元号 DA2，写入每条请求的头。默认 0（CPU 直接挂载）。</summary>
    public byte DestinationUnit { get; set; } = 0;

    /// <summary>字序与字符串编码；为 null 时使用 CDAB 字序与 ASCII 字符串（P4-D11）。</summary>
    public PlcDataFormat? DataFormat { get; set; }
}
