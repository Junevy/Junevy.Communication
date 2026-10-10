using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Melsec.Protocol;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Melsec;

/// <summary>
/// MELSEC 客户端选项的快照（P2-D5、D9、D15）：构造时校验并复制为不可变的值，之后修改选项对象不影响协议行为。
/// </summary>
internal sealed class McProtocolSettings
{
    private McProtocolSettings(McClientOptions options, PlcDataFormat dataFormat)
    {
        FrameType = options.FrameType;
        Encoding = options.Encoding;
        EthernetRoute = new McAccessRoute(options.NetworkNumber, options.PcNumber, options.ModuleIoNumber, options.ModuleStationNumber);
        SerialRoute = new Mc4CAccessRoute(
            options.StationNumber, options.NetworkNumber, options.PcNumber, options.ModuleIoNumber, options.ModuleStationNumber, options.SelfStationNumber);
        MonitoringTimer = options.MonitoringTimer;
        SumCheck = options.SumCheck;
        DataFormat = dataFormat;
    }

    /// <summary>帧类型。</summary>
    public McFrameType FrameType { get; }

    /// <summary>数据编码。</summary>
    public McEncoding Encoding { get; }

    /// <summary>3E / 4E 的访问路径。</summary>
    public McAccessRoute EthernetRoute { get; }

    /// <summary>4C 的访问路径。</summary>
    public Mc4CAccessRoute SerialRoute { get; }

    /// <summary>监视定时器（3E / 4E）。</summary>
    public ushort MonitoringTimer { get; }

    /// <summary>是否使用和校验（4C）。</summary>
    public bool SumCheck { get; }

    /// <summary>字序与字符串编码。</summary>
    public PlcDataFormat DataFormat { get; }

    /// <summary>是否为串口 4C 帧。</summary>
    public bool Is4C => FrameType == McFrameType.Frame4C;

    /// <summary>是否为 4E 以太网帧（带序列号）。</summary>
    public bool Is4E => FrameType == McFrameType.Frame4E;

    /// <summary>位访问的最大点数：串口 7904；以太网二进制 7168；以太网 ASCII 3584（计划 §18 的上限表）。</summary>
    public int MaxBitPoints => Is4C ? McLimits.SerialBitPoints
        : Encoding == McEncoding.Binary ? McLimits.EthernetBinaryBitPoints : McLimits.EthernetAsciiBitPoints;

    /// <summary>校验选项并取快照。</summary>
    /// <param name="options">客户端选项。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">帧类型、编码或监视定时器非法。</exception>
    public static McProtocolSettings From(McClientOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));
        if (!Enum.IsDefined(typeof(McFrameType), options.FrameType))
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameType, "The MC frame type is not defined.");
        if (!Enum.IsDefined(typeof(McEncoding), options.Encoding))
            throw new ArgumentOutOfRangeException(nameof(options), options.Encoding, "The MC encoding is not defined.");
        if (options.MonitoringTimer == 0)
            throw new ArgumentOutOfRangeException(nameof(McClientOptions.MonitoringTimer), options.MonitoringTimer,
                "The monitoring timer must be between 1 and 65535 (250 ms units).");

        return new McProtocolSettings(options, options.DataFormat ?? new PlcDataFormat(WordOrder.DCBA));
    }
}
