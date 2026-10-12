using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Plc;

namespace Junevy.Communication.Omron;

/// <summary>
/// FINS 连接参数的快照（P4-D18）：构造时校验并复制为不可变的值，之后修改选项对象不影响协议行为。
/// 客户端节点号的取值范围只在这里校验一次，<c>FinsNodeAddress</c> 只负责编码。
/// 节点号本身不在快照里：DA1 与 SA1 要等节点地址握手才有值，握手结果保存在会话中，
/// 由 <see cref="RouteWith"/> 与本类的网络号、单元号合成一条路由。
/// </summary>
internal sealed class FinsProtocolSettings
{
    // P4-D18：255 是 FINS 的广播地址，单节点客户端不能用它作源节点号。
    private const byte BroadcastNode = 0xFF;

    // 头的源网络号与源单元号固定为 0（P4-D18）。
    private const byte LocalNetworkAndUnit = 0;

    // FINS 的低字在前的双字布局对应套件的 CDAB（P4-D11），字符串默认 ASCII。
    private static readonly PlcDataFormat DefaultDataFormat = new PlcDataFormat(WordOrder.CDAB);

    private FinsProtocolSettings(byte clientNode, byte destinationNetwork, byte destinationUnit, PlcDataFormat dataFormat)
    {
        ClientNode = clientNode;
        DestinationNetwork = destinationNetwork;
        DestinationUnit = destinationUnit;
        DataFormat = dataFormat;
    }

    /// <summary>请求的客户端节点号；0 表示由 PLC 分配。</summary>
    public byte ClientNode { get; }

    /// <summary>DNA：目的网络号。</summary>
    public byte DestinationNetwork { get; }

    /// <summary>DA2：目的单元号。</summary>
    public byte DestinationUnit { get; }

    /// <summary>字序与字符串编码。</summary>
    public PlcDataFormat DataFormat { get; }

    /// <summary>校验连接参数并取快照。</summary>
    /// <param name="clientNode">客户端节点号，0 或 1–254。</param>
    /// <param name="destinationNetwork">DNA。</param>
    /// <param name="destinationUnit">DA2。</param>
    /// <param name="dataFormat">数据格式；为 null 时使用 CDAB 字序与 ASCII 字符串。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="clientNode"/> 是 FINS 的广播地址 255。</exception>
    public static FinsProtocolSettings Create(byte clientNode, byte destinationNetwork, byte destinationUnit, PlcDataFormat? dataFormat = null)
    {
        if (clientNode == BroadcastNode)
            throw new ArgumentOutOfRangeException(nameof(clientNode), clientNode,
                "The FINS client node 0xFF is the broadcast address and cannot be used as a source node.");

        return new FinsProtocolSettings(clientNode, destinationNetwork, destinationUnit, dataFormat ?? DefaultDataFormat);
    }

    /// <summary>校验选项并取快照（P1-D5、P4-D18）：广播节点号在这里拒绝。</summary>
    /// <param name="options">客户端选项。</param>
    /// <returns>快照。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="FinsClientOptions.ClientNode"/> 是 FINS 的广播地址 255。</exception>
    public static FinsProtocolSettings From(FinsClientOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        return Create(options.ClientNode, options.DestinationNetwork, options.DestinationUnit, options.DataFormat);
    }

    /// <summary>把握手得到的两个节点号与快照里的网络号、单元号合成路由（P4-D18）：DA1 取服务端节点，SA1 取客户端节点。</summary>
    /// <param name="numbers">握手应答里的节点号。</param>
    /// <returns>写请求用的路由。</returns>
    public FinsRoute RouteWith(FinsNodeNumbers numbers)
        => new FinsRoute(DestinationNetwork, numbers.ServerNode, DestinationUnit, LocalNetworkAndUnit, numbers.ClientNode, LocalNetworkAndUnit);
}
