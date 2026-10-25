using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens;

namespace Junevy.Communication.DebugAssistant.Protocols.Siemens;

/// <summary>
/// 西门子 S7 页签（计划第 7.4 节）：机架、槽号与连接类型（远端 TSAP），协商的 PDU 与单包上限，String 与 WString 的读写面板。
/// 读写与监控使用公共面板。S7 只经 ISO-on-TCP（端口 102）连接。
/// </summary>
public sealed partial class S7SessionViewModel : PlcSessionViewModel
{
    public S7SessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies, new ChannelSettingsEditor(ChannelTransport.Tcp, protocolOwnsChannelFraming: true), WordOrder.ABCD)
    {
        ReadStringCommand = new AsyncRelayCommand(ReadStringAsync);
        WriteStringCommand = new AsyncRelayCommand(WriteStringAsync);

        // 连接状态改变时刷新协商的 PDU：握手完成后才有。
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(State))
            {
                RefreshNegotiated();
            }
        };
        RefreshTsap();
        RefreshNegotiated();
    }

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 102;

    /// <summary>机架号，0 到 7。</summary>
    [ObservableProperty]
    private int _rack;

    /// <summary>槽号，0 到 31（S7-1500 常用 1，S7-1200 为 0 或 1）。</summary>
    [ObservableProperty]
    private int _slot = 1;

    [ObservableProperty]
    private S7ConnectionType _connectionType = S7ConnectionType.PG;

    /// <summary>远端 TSAP 的计算结果，或机架与槽号不合法的原因。</summary>
    [ObservableProperty]
    private string _remoteTsapText = string.Empty;

    /// <summary>协商的 PDU 与单包上限；未连接时说明。</summary>
    [ObservableProperty]
    private string _pduText = string.Empty;

    [ObservableProperty]
    private S7StringKind _stringKind = S7StringKind.String;

    /// <summary>字符串的最大长度（String 为 1 到 254，WString 为 1 到 16382）。</summary>
    [ObservableProperty]
    private int _stringMaxLength = 20;

    [ObservableProperty]
    private string _stringAddress = "DB1.DBB0";

    /// <summary>字符串的值：写入的文字，或读出的文字。</summary>
    [ObservableProperty]
    private string _stringValue = string.Empty;

    /// <summary>读取时头部的声明长度与实际长度。</summary>
    [ObservableProperty]
    private string _stringHeaderText = string.Empty;

    public IAsyncRelayCommand ReadStringCommand { get; }

    public IAsyncRelayCommand WriteStringCommand { get; }

    partial void OnRackChanged(int value) => RefreshTsap();

    partial void OnSlotChanged(int value) => RefreshTsap();

    partial void OnConnectionTypeChanged(S7ConnectionType value) => RefreshTsap();

    /// <summary>按机架与槽号创建 S7 客户端（TCP）；参数不合法时抛出 <see cref="ArgumentException"/>。</summary>
    protected override IPlcClient CreateClient()
    {
        if (S7Tsap.Problem(Rack, Slot) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        var options = new S7ClientOptions
        {
            Rack = Rack,
            Slot = Slot,
            ConnectionType = ConnectionType,
            DataFormat = Options.DataFormatOverride(),
        };
        Options.ApplyTo(options);
        return S7Client.Create(BuildTransportConfig(), Dependencies.Transports.Get(PlcTransport.Tcp), options, SessionLoggerFactory);
    }

    /// <summary>S7 的地址说明：数据块写出块号，然后是字节与位号。</summary>
    protected override string DescribeAddress(PlcAddress address)
    {
        string area = address is S7Address s && address.Device.Symbol == "DB" ? $"DB{s.DbNumber}" : address.Device.Symbol;
        return $"{area} 的字节 {address.Number}" + (address.BitIndex is { } bit ? $"，位 {bit}" : string.Empty) + "。";
    }

    public override SessionSettings CaptureSettings() => new S7Settings(
        CapturePanel(),
        Host,
        Port,
        Rack,
        Slot,
        ConnectionType,
        StringKind,
        StringMaxLength,
        StringAddress,
        StringValue);

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not S7Settings saved)
        {
            throw new ArgumentException("The settings do not belong to an S7 page.", nameof(settings));
        }

        Host = saved.Host;
        Port = saved.Port;
        Rack = saved.Rack;
        Slot = saved.Slot;
        ConnectionType = saved.ConnectionType;
        StringKind = saved.StringKind;
        StringMaxLength = saved.StringMaxLength;
        StringAddress = saved.StringAddress;
        StringValue = saved.StringValue;
        ApplyPanel(saved.Panel);
    }

    private async Task ReadStringAsync()
    {
        if (S7StringLimits.Problem(StringKind, StringMaxLength) is { } problem)
        {
            RecordInvalidString("读取字符串", problem);
            return;
        }

        if (CurrentClient is not S7Client client)
        {
            RecordNotConnected("读取字符串", StringAddress);
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult<string> text = StringKind == S7StringKind.String
            ? await client.ReadS7StringAsync(StringAddress, StringMaxLength, CancellationToken.None)
            : await client.ReadS7WStringAsync(StringAddress, StringMaxLength, CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "读取字符串", StringAddress, Since(started), text));
        if (!text.IsSuccess)
        {
            return;
        }

        StringValue = text.Data ?? string.Empty;

        // 库的字符串读取不返回头部，因此再读一次头部，显示声明长度与实际长度。
        DateTimeOffset headerStarted = Dependencies.Clock.GetLocalNow();
        CommResult<byte[]> header = await client.ReadBytesAsync(StringAddress, S7StringLimits.HeaderBytes(StringKind), CancellationToken.None);
        RecordOperation(OperationRecords.From(headerStarted, "读取字符串头部", StringAddress, Since(headerStarted), header));
        if (header.IsSuccess)
        {
            (int declared, int actual) = S7StringLimits.ReadHeader(StringKind, header.Data!);
            StringHeaderText = $"声明长度 {declared}，实际长度 {actual}";
        }
    }

    private async Task WriteStringAsync()
    {
        if (S7StringLimits.Problem(StringKind, StringMaxLength) is { } problem)
        {
            RecordInvalidString("写入字符串", problem);
            return;
        }

        if (CurrentClient is not S7Client client)
        {
            RecordNotConnected("写入字符串", StringAddress);
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult result = StringKind == S7StringKind.String
            ? await client.WriteS7StringAsync(StringAddress, StringValue, StringMaxLength, CancellationToken.None)
            : await client.WriteS7WStringAsync(StringAddress, StringValue, StringMaxLength, CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "写入字符串", StringAddress, Since(started), result));
    }

    private void RecordInvalidString(string operation, string message) =>
        RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), operation, StringAddress, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, message));

    private void RefreshTsap()
    {
        RemoteTsapText = S7Tsap.Problem(Rack, Slot) is { } problem
            ? problem
            : $"0x{S7Tsap.Remote(ConnectionType, Rack, Slot):X4}（本地 0x{S7Tsap.Local:X4}）";
    }

    /// <summary>已连接时显示协商的 PDU 与由它算出的单包上限；否则显示未连接。</summary>
    private void RefreshNegotiated()
    {
        if (CurrentClient is S7Client { NegotiatedPduSize: { } pdu })
        {
            S7PduLimit limit = S7PduLimits.For(pdu);
            PduText = $"协商的 PDU {pdu} 字节；单包上限：读 {limit.ReadBytes} 字节（{limit.ReadWords} 字），写 {limit.WriteBytes} 字节（{limit.WriteWords} 字）。";
        }
        else
        {
            PduText = "未连接。连接后显示协商的 PDU 与单包上限。";
        }
    }

    private IChannelConfig BuildTransportConfig() =>
        ChannelConfigs.TcpClient(Channel, Host, Port, localAddress: null, localPort: 0, protocolOwnsChannel: true);
}
