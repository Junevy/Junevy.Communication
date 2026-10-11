namespace Junevy.Communication.Siemens;

/// <summary>
/// S7 连接参数的快照（P4-D19）：构造时校验并复制为不可变的值，之后修改选项对象不影响协议行为。
/// 连接类型、Rack 与槽号的取值范围只在这里校验一次，<see cref="Protocol.CotpFrames"/> 只负责编码。
/// </summary>
internal sealed class S7ProtocolSettings
{
    // 连接类型取自 Snap7 的约定（P4-D19）：PG 1、OP 2、Basic 3。
    private const int MinimumConnectionType = 1;
    private const int MaximumConnectionType = 3;

    // 远端 TSAP 用 5 位放 Rack、8 位放槽号，因此上限是 7 与 31。
    private const int MaximumRack = 7;
    private const int MaximumSlot = 31;

    private S7ProtocolSettings(int connectionType, int rack, int slot)
    {
        ConnectionType = connectionType;
        Rack = rack;
        Slot = slot;
    }

    /// <summary>连接类型（PG 1、OP 2、Basic 3）。</summary>
    public int ConnectionType { get; }

    /// <summary>Rack 号。</summary>
    public int Rack { get; }

    /// <summary>槽号。</summary>
    public int Slot { get; }

    /// <summary>校验连接参数并取快照。</summary>
    /// <param name="connectionType">连接类型，1–3。</param>
    /// <param name="rack">Rack 号，0–7。</param>
    /// <param name="slot">槽号，0–31。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一参数超出 P4-D19 的范围。</exception>
    public static S7ProtocolSettings Create(int connectionType, int rack, int slot)
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

        return new S7ProtocolSettings(connectionType, rack, slot);
    }
}
