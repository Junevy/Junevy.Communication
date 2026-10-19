using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Omron;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Fins;

/// <summary>
/// 欧姆龙 FINS 页签（计划第 7.5 节）：客户端节点、目的网络与单元，握手得到的节点（请求固定节点而 CPU 给出不同节点时标出），
/// 区域速查表。读写与监控使用公共面板。FINS 只经 FINS/TCP（端口 9600）连接。
/// </summary>
public sealed partial class FinsSessionViewModel : PlcSessionViewModel
{
    public FinsSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies, new ChannelSettingsEditor(ChannelTransport.Tcp, protocolOwnsChannelFraming: true), WordOrder.CDAB)
    {
        // 连接状态改变时刷新握手结果：握手完成后才有节点号。
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(State))
            {
                RefreshHandshake();
            }
        };
        RefreshHandshake();
    }

    /// <summary>区域速查表（18 项）。</summary>
    public IReadOnlyList<FinsAreaEntry> Areas => FinsAreas.All;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 9600;

    /// <summary>请求的客户端节点：0 由 CPU 分配，1 到 254 为固定节点。</summary>
    [ObservableProperty]
    private int _clientNode;

    /// <summary>目的网络号 DNA，0 表示与 CPU 同一网络。</summary>
    [ObservableProperty]
    private int _destinationNetwork;

    /// <summary>目的单元号 DA2，0 表示 CPU 直接挂载。</summary>
    [ObservableProperty]
    private int _destinationUnit;

    /// <summary>握手结果：客户端与服务端节点；请求固定节点而 CPU 给出不同节点时说明差异。</summary>
    [ObservableProperty]
    private string _handshakeText = string.Empty;

    /// <summary>按请求的节点与路由创建 FINS 客户端（TCP）；参数不合法时抛出 <see cref="ArgumentException"/>。</summary>
    protected override IPlcClient CreateClient()
    {
        if (FinsNodes.ClientNodeProblem(ClientNode) is { } clientProblem)
        {
            throw new ArgumentException(clientProblem);
        }

        if (FinsNodes.ByteProblem("目的网络号", DestinationNetwork) is { } networkProblem)
        {
            throw new ArgumentException(networkProblem);
        }

        if (FinsNodes.ByteProblem("目的单元号", DestinationUnit) is { } unitProblem)
        {
            throw new ArgumentException(unitProblem);
        }

        var options = new FinsClientOptions
        {
            ClientNode = (byte)ClientNode,
            DestinationNetwork = (byte)DestinationNetwork,
            DestinationUnit = (byte)DestinationUnit,
            DataFormat = Options.DataFormatOverride(),
        };
        Options.ApplyTo(options);
        return FinsClient.Create(BuildTransportConfig(), Dependencies.Transports.Get(PlcTransport.Tcp), options, SessionLoggerFactory);
    }

    public override SessionSettings CaptureSettings() => new FinsSettings(
        CapturePanel(),
        Host,
        Port,
        ClientNode,
        DestinationNetwork,
        DestinationUnit);

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not FinsSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a FINS page.", nameof(settings));
        }

        Host = saved.Host;
        Port = saved.Port;
        ClientNode = saved.ClientNode;
        DestinationNetwork = saved.DestinationNetwork;
        DestinationUnit = saved.DestinationUnit;
        ApplyPanel(saved.Panel);
    }

    /// <summary>已连接时显示握手得到的节点；请求固定节点（1 到 254）而 CPU 给出不同节点时，说明差异。</summary>
    private void RefreshHandshake()
    {
        if (CurrentClient is not FinsClient { ClientNode: { } clientNode } client)
        {
            HandshakeText = "未连接。连接后显示握手得到的节点号。";
            return;
        }

        string text = $"客户端节点 {clientNode}，服务端节点 {client.ServerNode?.ToString() ?? "未知"}。";
        HandshakeText = ClientNode is >= 1 and <= 254 && clientNode != ClientNode
            ? text + $"注意：请求的客户端节点为 {ClientNode}，CPU 给出了不同的节点 {clientNode}。"
            : text;
    }

    private IChannelConfig BuildTransportConfig() =>
        ChannelConfigs.TcpClient(Channel, Host, Port, localAddress: null, localPort: 0, protocolOwnsChannel: true);
}
