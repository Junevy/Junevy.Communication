using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Modbus;

/// <summary>
/// Modbus 页签（计划第 7.2 节）：传输与帧格式（组合名称、"串口 + MBAP"不可选）、站号与临时站号、串口参数、Modbus 选项；
/// 协议特有的区域包括功能码提示、掩码写（0x16）、读写多个寄存器（0x17）与结果表格。读写与监控使用公共面板。
/// </summary>
public sealed partial class ModbusSessionViewModel : PlcSessionViewModel
{
    private const int MaxTableRows = 2000;

    public ModbusSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies, new ChannelSettingsEditor(ChannelTransport.Tcp, protocolOwnsChannelFraming: true), WordOrder.ABCD)
    {
        ApplyMaskCommand = new AsyncRelayCommand(ApplyMaskAsync);
        SetMaskBitCommand = new RelayCommand(() => SetMask(set: true));
        ClearMaskBitCommand = new RelayCommand(() => SetMask(set: false));
        ReadWriteCommand = new AsyncRelayCommand(ReadWriteAsync);
        ReadTableCommand = new AsyncRelayCommand(ReadTableAsync);

        // 功能码提示依赖地址、数量与选项；地址与数量由公共面板修改，因此订阅自己的属性变化。
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Address) or nameof(Count) or nameof(ValueType) or nameof(UseSingleWriteFunctions))
            {
                RefreshFunctionHint();
            }
        };
        RefreshCombination();
        RefreshFunctionHint();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial))]
    private PlcTransport _transport = PlcTransport.Tcp;

    /// <summary>是否为串口传输：界面据此只显示主机与端口，或只显示串口参数。</summary>
    public bool IsSerial => Transport == PlcTransport.Serial;

    [ObservableProperty]
    private ModbusFrameChoice _frame = ModbusFrameChoice.Auto;

    /// <summary>组合的名称，或不合法的原因。</summary>
    [ObservableProperty]
    private string _combinationName = string.Empty;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 502;

    /// <summary>默认站号，地址没有站号前缀时使用；RTU 时为 1 到 247。</summary>
    [ObservableProperty]
    private byte _unitId = 1;

    /// <summary>临时站号：把 <c>s=N;</c> 前缀拼进地址，留空则不拼。</summary>
    [ObservableProperty]
    private int? _temporaryStation;

    [ObservableProperty]
    private string _portName = string.Empty;

    [ObservableProperty]
    private int _baudRate = 9600;

    [ObservableProperty]
    private int _dataBits = 8;

    [ObservableProperty]
    private Parity _parity = Parity.None;

    [ObservableProperty]
    private StopBits _stopBits = StopBits.One;

    [ObservableProperty]
    private bool _useSingleWriteFunctions = true;

    [ObservableProperty]
    private bool _useMaskWriteForBits;

    /// <summary>当前地址、数量与选项下的功能码提示。</summary>
    [ObservableProperty]
    private string _functionHint = string.Empty;

    /// <summary>掩码写的与掩码（十六进制或十进制）。</summary>
    [ObservableProperty]
    private string _maskAndText = "0xFFFF";

    /// <summary>掩码写的或掩码（十六进制或十进制）。</summary>
    [ObservableProperty]
    private string _maskOrText = "0x0000";

    /// <summary>助手的位号，0 到 15。</summary>
    [ObservableProperty]
    private int _maskBit;

    [ObservableProperty]
    private string _readWriteReadAddress = "HR0";

    [ObservableProperty]
    private int _readWriteReadCount = 1;

    [ObservableProperty]
    private string _readWriteWriteAddress = "HR0";

    /// <summary>读写多个寄存器写入的字节（<c>hex:</c> 或文本语法），每两个字节一个寄存器。</summary>
    [ObservableProperty]
    private string _readWriteWriteText = "hex:00 00";

    [ObservableProperty]
    private string _tableAddress = "HR0";

    [ObservableProperty]
    private int _tableCount = 10;

    /// <summary>结果表格，由"读取表格"填充。</summary>
    public ObservableCollection<ModbusTableRow> Table { get; } = [];

    public IAsyncRelayCommand ApplyMaskCommand { get; }

    public IRelayCommand SetMaskBitCommand { get; }

    public IRelayCommand ClearMaskBitCommand { get; }

    public IAsyncRelayCommand ReadWriteCommand { get; }

    public IAsyncRelayCommand ReadTableCommand { get; }

    /// <summary>传输改变时，通道参数视图随之切换（套接字与 TLS 只属于 TCP），串口不能使用 MBAP 时改用 RTU。</summary>
    partial void OnTransportChanged(PlcTransport value)
    {
        Channel.Transport = value switch
        {
            PlcTransport.Udp => ChannelTransport.Udp,
            PlcTransport.Serial => ChannelTransport.Serial,
            _ => ChannelTransport.Tcp,
        };
        if (ModbusCombination.Problem(value, Frame) is not null)
        {
            Frame = ModbusFrameChoice.Rtu;
        }

        RefreshCombination();
    }

    partial void OnFrameChanged(ModbusFrameChoice value) => RefreshCombination();

    /// <summary>临时站号拼进地址：已经有 <c>s=</c> 前缀的地址不再拼接。</summary>
    protected override string ToProtocolAddress(string address)
    {
        string trimmed = address.Trim();
        if (TemporaryStation is not { } station || trimmed.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"s={station};{trimmed}";
    }

    /// <summary>按传输与帧格式创建 Modbus 客户端：通道配置由传输决定，分帧由协议决定（<c>Framing</c> 由组合给出）。</summary>
    protected override IPlcClient CreateClient()
    {
        if (ModbusCombination.Problem(Transport, Frame) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        var options = new ModbusClientOptions
        {
            Framing = ModbusCombination.ToFraming(Frame),
            UnitId = UnitId,
            UseSingleWriteFunctions = UseSingleWriteFunctions,
            UseMaskWriteForBits = UseMaskWriteForBits,
            DataFormat = Options.DataFormatOverride(),
        };
        Options.ApplyTo(options);
        return ModbusClient.Create(BuildTransportConfig(), Dependencies.Transports.Get(Transport), options, SessionLoggerFactory);
    }

    public override SessionSettings CaptureSettings() => new ModbusSettings(
        CapturePanel(),
        Transport,
        Frame,
        Host,
        Port,
        UnitId,
        PortName,
        BaudRate,
        DataBits,
        Parity,
        StopBits,
        UseSingleWriteFunctions,
        UseMaskWriteForBits);

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not ModbusSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a Modbus page.", nameof(settings));
        }

        Transport = saved.Transport;
        Frame = saved.Frame;
        Host = saved.Host;
        Port = saved.Port;
        UnitId = saved.UnitId;
        PortName = saved.PortName;
        BaudRate = saved.BaudRate;
        DataBits = saved.DataBits;
        Parity = saved.Parity;
        StopBits = saved.StopBits;
        UseSingleWriteFunctions = saved.UseSingleWriteFunctions;
        UseMaskWriteForBits = saved.UseMaskWriteForBits;
        ApplyPanel(saved.Panel);
    }

    private IChannelConfig BuildTransportConfig() => Transport switch
    {
        PlcTransport.Udp => ChannelConfigs.Udp(
            Channel, "0.0.0.0", 0, Host, Port, enableBroadcast: false, multicastGroups: null, multicastTimeToLive: 1,
            multicastLoopback: false, reuseAddress: false, requestRetryCount: 0, maxDatagramSize: 65507, protocolOwnsChannel: true),
        PlcTransport.Serial => ChannelConfigs.Serial(
            Channel, PortName, BaudRate, DataBits, Parity, StopBits, Handshake.None, dtrEnable: false, rtsEnable: false,
            readBufferSize: 4096, writeBufferSize: 2048, openTimeout: 2000, protocolOwnsChannel: true),
        _ => ChannelConfigs.TcpClient(Channel, Host, Port, localAddress: null, localPort: 0, protocolOwnsChannel: true),
    };

    private void RefreshCombination()
    {
        CombinationName = ModbusCombination.Name(Transport, Frame)
            ?? ModbusCombination.Problem(Transport, Frame)
            ?? string.Empty;
    }

    private void RefreshFunctionHint()
    {
        // 地址为空时公共面板已提示"请输入地址"，这里不再重复库的解析错误。
        if (string.IsNullOrWhiteSpace(Address))
        {
            FunctionHint = string.Empty;
            return;
        }

        CommResult<PlcAddress> parsed = ParseAddressText(ToProtocolAddress(Address));
        FunctionHint = parsed.IsSuccess
            ? ModbusFunctionHint.Describe(parsed.Data!.Device.Symbol, Count, UseSingleWriteFunctions)
            : parsed.ErrorMessage;
    }

    private void SetMask(bool set)
    {
        try
        {
            (ushort andMask, ushort orMask) = set ? ModbusMask.SetBit(MaskBit) : ModbusMask.ClearBit(MaskBit);
            MaskAndText = $"0x{andMask:X4}";
            MaskOrText = $"0x{orMask:X4}";
        }
        catch (ArgumentOutOfRangeException ex)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "掩码", Address, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, ex.Message));
        }
    }

    private async Task ApplyMaskAsync()
    {
        if (CurrentClient is not ModbusClient client)
        {
            RecordNotConnected("掩码写", Address);
            return;
        }

        if (!TryParseWord(MaskAndText, out ushort andMask) || !TryParseWord(MaskOrText, out ushort orMask))
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "掩码写", Address, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null,
                "掩码必须是 0 到 0xFFFF 之间的数字（十进制，或 0x 开头的十六进制）。"));
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult result = await client.MaskWriteRegisterAsync(ToProtocolAddress(Address), andMask, orMask, CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "掩码写", Address, TimeSpan.Zero, result));
    }

    private async Task ReadWriteAsync()
    {
        if (CurrentClient is not ModbusClient client)
        {
            RecordNotConnected("读写多个", ReadWriteReadAddress);
            return;
        }

        byte[] writeData;
        try
        {
            writeData = ByteInput.Parse(ReadWriteWriteText, EncodingChoice.Utf8);
        }
        catch (FormatException ex)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "读写多个", ReadWriteWriteAddress, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null, ex.Message));
            return;
        }

        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        CommResult<byte[]> result = await client.ReadWriteMultipleRegistersAsync(
            ToProtocolAddress(ReadWriteReadAddress), ReadWriteReadCount, ToProtocolAddress(ReadWriteWriteAddress), writeData, CancellationToken.None);
        RecordOperation(OperationRecords.From(started, "读写多个", ReadWriteReadAddress, TimeSpan.Zero, result));
        if (result.IsSuccess && ParseAddressText(ToProtocolAddress(ReadWriteReadAddress)) is { IsSuccess: true, Data: { } address })
        {
            ShowWords(address.Device.Symbol, address.Number, result.Data!);
        }
    }

    private async Task ReadTableAsync()
    {
        if (CurrentClient is not { } client)
        {
            RecordNotConnected("读取表格", TableAddress);
            return;
        }

        if (TableCount is < 1 or > MaxTableRows)
        {
            RecordOperation(new OperationRecord(Dependencies.Clock.GetLocalNow(), "读取表格", TableAddress, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null,
                $"数量必须在 1 到 {MaxTableRows} 之间。"));
            return;
        }

        CommResult<PlcAddress> parsed = ParseAddressText(ToProtocolAddress(TableAddress));
        if (!parsed.IsSuccess)
        {
            RecordOperation(OperationRecords.From(Dependencies.Clock.GetLocalNow(), "读取表格", TableAddress, TimeSpan.Zero, parsed));
            return;
        }

        PlcAddress address = parsed.Data!;
        string symbol = address.Device.Symbol;
        string protocolAddress = ToProtocolAddress(TableAddress);
        DateTimeOffset started = Dependencies.Clock.GetLocalNow();
        switch (symbol)
        {
            case "C" or "DI":
            {
                CommResult<bool[]> bits = await client.ReadBitsAsync(protocolAddress, TableCount, CancellationToken.None);
                RecordOperation(OperationRecords.From(started, "读取表格", TableAddress, TimeSpan.Zero, bits));
                if (bits.IsSuccess)
                {
                    ShowBits(symbol, address.Number, bits.Data!);
                }

                break;
            }

            case "HR" or "IR":
            {
                CommResult<byte[]> bytes = await client.ReadAsync(protocolAddress, TableCount, CancellationToken.None);
                RecordOperation(OperationRecords.From(started, "读取表格", TableAddress, TimeSpan.Zero, bytes));
                if (bytes.IsSuccess)
                {
                    ShowWords(symbol, address.Number, bytes.Data!);
                }

                break;
            }

            default:
                RecordOperation(new OperationRecord(started, "读取表格", TableAddress, TimeSpan.Zero, false, CommErrorKind.InvalidRequest, null,
                    "表格读取只支持 C、DI、HR 与 IR。"));
                break;
        }
    }

    /// <summary>位（线圈与离散输入）显示为表格行，每个位一行。</summary>
    private void ShowBits(string symbol, int firstNumber, bool[] bits)
    {
        Table.Clear();
        for (int index = 0; index < bits.Length; index++)
        {
            Table.Add(ModbusTableRow.ForBit(symbol, firstNumber + index, bits[index]));
        }
    }

    /// <summary>寄存器的字节（每个寄存器两个字节，大端）显示为表格行。</summary>
    private void ShowWords(string symbol, int firstNumber, byte[] bytes)
    {
        Table.Clear();
        for (int index = 0; index + 1 < bytes.Length; index += 2)
        {
            ushort value = (ushort)((bytes[index] << 8) | bytes[index + 1]);
            Table.Add(ModbusTableRow.ForWord(symbol, firstNumber + index / 2, value));
        }
    }

    private static bool TryParseWord(string text, out ushort value)
    {
        try
        {
            value = ((ushort[])PlcValueCodec.Parse(PlcValueType.UInt16, text, EncodingChoice.Utf8))[0];
            return true;
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }
}
