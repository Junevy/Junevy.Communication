using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 连接参数的快照（P4-D19）：构造时校验并复制为不可变的值，之后修改选项对象不影响协议行为。
/// 连接类型、Rack 与槽号的取值范围只在这里校验一次，<see cref="Protocol.CotpFrames"/> 只负责编码。
/// 数据格式不在这里校验：<see cref="PlcDataFormat"/> 自己在构造时拒绝未定义的字序。
/// </summary>
internal sealed class S7ProtocolSettings
{
    // 连接类型取自 Snap7 的约定（P4-D19）：PG 1、OP 2、Basic 3。
    private const int MinimumConnectionType = 1;
    private const int MaximumConnectionType = 3;

    // 远端 TSAP 用 5 位放 Rack、8 位放槽号，因此上限是 7 与 31。
    private const int MaximumRack = 7;
    private const int MaximumSlot = 31;

    // S7 的设备内存顺序就是套件的规范化布局（P4-D11），因此默认字序 ABCD、字符串 ASCII。
    private static readonly PlcDataFormat DefaultDataFormat = new PlcDataFormat(WordOrder.ABCD);

    private S7ProtocolSettings(int connectionType, int rack, int slot, PlcDataFormat dataFormat)
    {
        ConnectionType = connectionType;
        Rack = rack;
        Slot = slot;
        DataFormat = dataFormat;
    }

    /// <summary>连接类型（PG 1、OP 2、Basic 3）。</summary>
    public int ConnectionType { get; }

    /// <summary>Rack 号。</summary>
    public int Rack { get; }

    /// <summary>槽号。</summary>
    public int Slot { get; }

    /// <summary>字序与字符串编码。</summary>
    public PlcDataFormat DataFormat { get; }

    /// <summary>校验连接参数并取快照。</summary>
    /// <param name="connectionType">连接类型，1–3。</param>
    /// <param name="rack">Rack 号，0–7。</param>
    /// <param name="slot">槽号，0–31。</param>
    /// <param name="dataFormat">数据格式；为 null 时使用 ABCD 字序与 ASCII 字符串。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一连接参数超出 P4-D19 的范围。</exception>
    public static S7ProtocolSettings Create(int connectionType, int rack, int slot, PlcDataFormat? dataFormat = null)
    {
        if (connectionType < MinimumConnectionType || connectionType > MaximumConnectionType)
            throw new ArgumentOutOfRangeException(nameof(connectionType), connectionType,
                $"The S7 connection type must be between {MinimumConnectionType} and {MaximumConnectionType} (PG, OP, Basic).");
        if (rack < 0 || rack > MaximumRack)
            throw new ArgumentOutOfRangeException(nameof(rack), rack,
                $"The S7 rack must be between 0 and {MaximumRack}.");
        if (slot < 0 || slot > MaximumSlot)
            throw new ArgumentOutOfRangeException(nameof(slot), slot,
                $"The S7 slot must be between 0 and {MaximumSlot}.");

        return new S7ProtocolSettings(connectionType, rack, slot, dataFormat ?? DefaultDataFormat);
    }

    /// <summary>校验选项并取快照（P1-D5、P4-D19）：未定义的连接类型与超出范围的 Rack、槽号都在这里拒绝。</summary>
    /// <param name="options">客户端选项。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">连接类型未定义，或 Rack、槽号超出 P4-D19 的范围。</exception>
    public static S7ProtocolSettings From(S7ClientOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (!Enum.IsDefined(typeof(S7ConnectionType), options.ConnectionType))
            throw new ArgumentOutOfRangeException(nameof(options.ConnectionType), options.ConnectionType, "The S7 connection type is not defined.");

        return Create((int)options.ConnectionType, options.Rack, options.Slot, options.DataFormat);
    }
}
