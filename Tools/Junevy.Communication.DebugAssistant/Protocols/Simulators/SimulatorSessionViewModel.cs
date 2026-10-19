using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Melsec;
using Junevy.Communication.DebugAssistant.Simulators.Memory;
using Junevy.Communication.DebugAssistant.Simulators.Modbus;
using Junevy.Communication.DebugAssistant.Simulators.Omron;
using Junevy.Communication.DebugAssistant.Simulators.Siemens;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Protocols.Simulators;

/// <summary>模拟设备的种类：Modbus 从站、MC 从站、S7 CPU 与 FINS 从站。</summary>
public enum SimulatedDevice
{
    Modbus,
    Mc,
    S7,
    Fins,
}

/// <summary>内存表的一行（字区）：地址，以及字的无符号、有符号、十六进制与二进制显示。</summary>
public sealed record SimulatorMemoryRow(int Address, ushort Unsigned, short Signed, string Hex, string Binary);

/// <summary>内存表的一行（位区）：起始地址，以及从该地址起的 16 个位。</summary>
public sealed record SimulatorBitRow(int Address, string Bits);

/// <summary>内存表的一行（字节区）：起始地址，以及从该地址起的 16 个字节（十六进制）。</summary>
public sealed record SimulatorByteRow(int Address, string Bytes);

/// <summary>请求日志的一行：时间、对端、结论、摘要，以及请求与应答的十六进制。</summary>
public sealed record SimulatorRequestRow(string Time, string Remote, string Kind, string Summary, string Request, string Response);

/// <summary>
/// 模拟设备页签（计划第 7.14 节）：宿主参数、设备参数、内存表（字区按字显示、可分页、可编辑、可自增；位区与字节区每行 16 个、只读）、
/// 故障注入与请求日志。启动即挂载引擎到宿主上；停止即关闭监听。引擎与内存在页签内持有，切换设备参数需要重新启动。
/// </summary>
public sealed partial class SimulatorSessionViewModel : SessionViewModel
{
    private const int PageSize = 64;
    private const int ElementsPerRow = 16;
    private const int RowsPerPage = 64;
    private const int ElementsPerPage = ElementsPerRow * RowsPerPage;
    private const int LogCapacity = 500;
    private const int AutoIncrementMilliseconds = 500;
    private const int MaxFaultDelayMilliseconds = 60000;
    private const int MaxDataBlockNumber = 65535;
    private const int MaxDataBlockSize = 65536;

    private readonly SimulatedDevice _device;
    private readonly SimulatorMemory _memory = new();
    private readonly FaultInjector _faults = new();
    private SimulatorHost? _listener;
    private CancellationTokenSource? _autoCancellation;

    public SimulatorSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies)
    {
        _device = descriptor.Kind switch
        {
            ProtocolKind.ModbusSlave => SimulatedDevice.Modbus,
            ProtocolKind.McSlave => SimulatedDevice.Mc,
            ProtocolKind.S7Cpu => SimulatedDevice.S7,
            ProtocolKind.FinsSlave => SimulatedDevice.Fins,
            _ => throw new ArgumentException("不是模拟设备的种类。", nameof(descriptor)),
        };
        Port = _device switch
        {
            SimulatedDevice.Modbus => 502,
            SimulatedDevice.Mc => 5000,
            SimulatedDevice.S7 => 102,
            _ => 9600,
        };
        TransportChoices = _device is SimulatedDevice.S7 or SimulatedDevice.Fins
            ? [SimulatorTransport.Tcp]
            : [SimulatorTransport.Tcp, SimulatorTransport.Udp, SimulatorTransport.Serial];
        Transport = TransportChoices[0];

        // 先按当前设置创建引擎，让引擎登记它的存储区（内存表需要它们）。
        _ = BuildEngine();
        foreach (string name in _memory.AreaNames())
        {
            MemoryAreas.Add(name);
        }

        SelectedArea = MemoryAreas.FirstOrDefault() ?? string.Empty;

        RefreshMemoryCommand = new RelayCommand(RefreshMemory);
        NextPageCommand = new RelayCommand(() => ChangePage(1));
        PreviousPageCommand = new RelayCommand(() => ChangePage(-1));
        WriteWordCommand = new RelayCommand(WriteWord);
        AddDataBlockCommand = new RelayCommand(AddDataBlock);
        ApplyFaultsCommand = new RelayCommand(ApplyFaults);
        ClearFaultsCommand = new RelayCommand(ClearFaults);
        PropertyChanged += (_, e) => OnSettingChanged(e.PropertyName);
        RefreshCombination();
        RefreshMemory();
    }

    /// <summary>传输方式的可选值：MC 与 Modbus 有 TCP、UDP 与串口；S7 与 FINS 只有 TCP。</summary>
    public IReadOnlyList<SimulatorTransport> TransportChoices { get; }

    /// <summary>全部存储区的名称（字区、位区、字节区）；S7 新建的数据块也加入这里。</summary>
    public ObservableCollection<string> MemoryAreas { get; } = [];

    /// <summary>设备种类，供视图显示对应的设备参数。</summary>
    public bool IsModbusDevice => _device == SimulatedDevice.Modbus;

    public bool IsMcDevice => _device == SimulatedDevice.Mc;

    public bool IsS7Device => _device == SimulatedDevice.S7;

    public bool IsFinsDevice => _device == SimulatedDevice.Fins;

    public bool IsSerialTransport => Transport == SimulatorTransport.Serial;

    /// <summary>请求日志，最新的在最前。</summary>
    public ObservableCollection<SimulatorRequestRow> Requests { get; } = [];

    /// <summary>内存表的当前页：字区的行。</summary>
    public ObservableCollection<SimulatorMemoryRow> WordRows { get; } = [];

    /// <summary>内存表的当前页：位区的行。</summary>
    public ObservableCollection<SimulatorBitRow> BitRows { get; } = [];

    /// <summary>内存表的当前页：字节区的行。</summary>
    public ObservableCollection<SimulatorByteRow> ByteRows { get; } = [];

    [ObservableProperty]
    private SimulatorTransport _transport;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private string _portName = string.Empty;

    [ObservableProperty]
    private int _baudRate = 9600;

    /// <summary>应答的站号列表，逗号分隔（Modbus）。</summary>
    [ObservableProperty]
    private string _unitIds = "1";

    [ObservableProperty]
    private bool _modbusRtu;

    [ObservableProperty]
    private McFrameKind _mcFrame = McFrameKind.Frame3E;

    [ObservableProperty]
    private McEncodingKind _mcEncoding = McEncodingKind.Binary;

    [ObservableProperty]
    private bool _mcSumCheck = true;

    [ObservableProperty]
    private int _s7Pdu = 240;

    [ObservableProperty]
    private bool _s7PutGet = true;

    [ObservableProperty]
    private int _finsServerNode = 1;

    [ObservableProperty]
    private int _finsClientNode = 0x0B;

    /// <summary>当前组合的说明，例如 "Modbus TCP（MBAP）"。</summary>
    [ObservableProperty]
    private string _combinationText = string.Empty;

    [ObservableProperty]
    private string _selectedArea = string.Empty;

    [ObservableProperty]
    private bool _isWordArea;

    [ObservableProperty]
    private bool _isBitArea;

    [ObservableProperty]
    private bool _isByteArea;

    [ObservableProperty]
    private int _pageIndex;

    [ObservableProperty]
    private string _memoryPageText = string.Empty;

    /// <summary>写入字时的说明：不合法的地址或数值，写入成功后清空。</summary>
    [ObservableProperty]
    private string _memoryMessage = string.Empty;

    [ObservableProperty]
    private int _editAddress;

    [ObservableProperty]
    private string _editValue = "0";

    /// <summary>自增：所选字区的每个字每 500 毫秒加 1，用来模拟变化的数据。</summary>
    [ObservableProperty]
    private bool _autoIncrement;

    /// <summary>新建数据块的编号（S7），十进制，1 到 65535。</summary>
    [ObservableProperty]
    private string _newDataBlockNumber = "1";

    /// <summary>新建数据块的大小（字节），十进制，1 到 65536。</summary>
    [ObservableProperty]
    private string _newDataBlockSize = "256";

    [ObservableProperty]
    private string _dataBlockMessage = string.Empty;

    [ObservableProperty]
    private int _faultDelayMilliseconds;

    [ObservableProperty]
    private bool _faultNoReply;

    /// <summary>强制错误码（十六进制或十进制），留空表示不强制。</summary>
    [ObservableProperty]
    private string _faultForcedErrorText = string.Empty;

    [ObservableProperty]
    private bool _faultCorruptChecksum;

    /// <summary>收到下一个请求后断开连接（一次性）。</summary>
    [ObservableProperty]
    private bool _faultDisconnectNext;

    [ObservableProperty]
    private string _faultMessage = string.Empty;

    public IRelayCommand RefreshMemoryCommand { get; }

    public IRelayCommand NextPageCommand { get; }

    public IRelayCommand PreviousPageCommand { get; }

    public IRelayCommand WriteWordCommand { get; }

    public IRelayCommand AddDataBlockCommand { get; }

    public IRelayCommand ApplyFaultsCommand { get; }

    public IRelayCommand ClearFaultsCommand { get; }

    public override string ConnectText => "启动";

    public override string DisconnectText => "停止";

    partial void OnSelectedAreaChanged(string value)
    {
        PageIndex = 0;
        RefreshMemory();
    }

    partial void OnAutoIncrementChanged(bool value)
    {
        _autoCancellation?.Cancel();
        _autoCancellation?.Dispose();
        _autoCancellation = null;
        if (value)
        {
            _autoCancellation = new CancellationTokenSource();
            _ = IncrementAsync(_autoCancellation.Token);
        }
    }

    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        IChannelConfig config;
        ISimulatorEngine engine;
        try
        {
            config = BuildConfig();
            engine = BuildEngine();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new CommFailureException(CommResult.Fail(ex.Message, CommErrorKind.InvalidRequest));
        }

        var host = new SimulatorHost(config, engine, _faults, SessionLoggerFactory);
        host.RequestHandled += (_, e) => PostToUi(() => AddRequest(e));
        CommResult started = await host.StartAsync(cancellationToken).ConfigureAwait(true);
        if (!started.IsSuccess)
        {
            await host.DisposeAsync();
            throw new CommFailureException(started);
        }

        _listener = host;
        State = SessionState.Running;
    }

    protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_listener is not { } host)
        {
            return;
        }

        _listener = null;
        State = SessionState.Stopped;
        await host.StopAsync(cancellationToken).ConfigureAwait(true);
        await host.DisposeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            _autoCancellation?.Cancel();
            _autoCancellation?.Dispose();
            if (_listener is { } host)
            {
                _listener = null;
                await host.DisposeAsync();
            }
        }
    }

    public override SessionSettings CaptureSettings() => new SimulatorSettings(
        Transport,
        Host,
        Port,
        PortName,
        BaudRate,
        UnitIds,
        ModbusRtu,
        McFrame,
        McEncoding,
        McSumCheck,
        S7Pdu,
        S7PutGet,
        FinsServerNode,
        FinsClientNode);

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not SimulatorSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a simulator page.", nameof(settings));
        }

        Transport = saved.Transport;
        Host = saved.Host;
        Port = saved.Port;
        PortName = saved.PortName;
        BaudRate = saved.BaudRate;
        UnitIds = saved.UnitIds;
        ModbusRtu = saved.ModbusRtu;
        McFrame = saved.McFrame;
        McEncoding = saved.McEncoding;
        McSumCheck = saved.McSumCheck;
        S7Pdu = saved.S7Pdu;
        S7PutGet = saved.S7PutGet;
        FinsServerNode = saved.FinsServerNode;
        FinsClientNode = saved.FinsClientNode;
    }

    private IChannelConfig BuildConfig() => Transport switch
    {
        SimulatorTransport.Udp => new UdpChannelConfig { LocalAddress = Host, LocalPort = Port },
        SimulatorTransport.Serial => new SerialChannelConfig { PortName = PortName, BaudRate = BaudRate },
        _ => new TcpServerConfig { ListenAddress = Host, Port = Port },
    };

    /// <summary>
    /// 按设备与设置创建引擎；设置不合法时抛出 <see cref="ArgumentException"/>。串口上的 Modbus 只能是 RTU，串口上的 MC 只能是 4C；
    /// 这两者由传输决定，与界面上的帧设置无关。
    /// </summary>
    private ISimulatorEngine BuildEngine() => _device switch
    {
        SimulatedDevice.Modbus => new ModbusSlaveEngine(_memory, new ModbusSlaveOptions(ParseUnitIds(), ModbusRtu || Transport == SimulatorTransport.Serial ? ModbusFrameMode.Rtu : ModbusFrameMode.Mbap)),
        SimulatedDevice.Mc => new McSlaveEngine(_memory, new McSlaveOptions(Transport == SimulatorTransport.Serial ? McFrameKind.Frame4C : McFrame, McEncoding, McSumCheck)),
        SimulatedDevice.S7 => new S7CpuEngine(_memory, new S7CpuOptions(S7Pdu, S7PutGet)),
        _ => new FinsSlaveEngine(_memory, new FinsSlaveOptions(ToByte(FinsServerNode, "服务器节点"), ToByte(FinsClientNode, "客户端节点"))),
    };

    private byte[] ParseUnitIds()
    {
        string[] parts = UnitIds.Split([',', ' ', '，'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("至少需要一个站号。");
        }

        return parts.Select(part => ToByte(int.Parse(part, CultureInfo.InvariantCulture), "站号")).ToArray();
    }

    private static byte ToByte(int value, string name) =>
        value is >= 0 and <= 255 ? (byte)value : throw new ArgumentException($"{name} 必须在 0 到 255 之间。");

    private void OnSettingChanged(string? name)
    {
        if (name == nameof(Transport))
        {
            OnPropertyChanged(nameof(IsSerialTransport));
        }

        if (name is nameof(Transport) or nameof(ModbusRtu) or nameof(McFrame) or nameof(McEncoding) or nameof(McSumCheck) or nameof(S7Pdu) or nameof(FinsServerNode) or nameof(FinsClientNode) or nameof(Port) or nameof(Host))
        {
            RefreshCombination();
        }
    }

    private void RefreshCombination()
    {
        CombinationText = _device switch
        {
            SimulatedDevice.Modbus => Transport switch
            {
                SimulatorTransport.Tcp => ModbusRtu ? "RTU over TCP" : "Modbus TCP（MBAP）",
                SimulatorTransport.Udp => ModbusRtu ? "RTU over UDP" : "Modbus UDP（MBAP）",
                _ => "Modbus RTU（串口）",
            },
            SimulatedDevice.Mc => Transport == SimulatorTransport.Serial
                ? $"4C 格式 {(McEncoding == McEncodingKind.Ascii ? 4 : 5)}（串口{(McSumCheck ? "，和校验" : string.Empty)}）"
                : $"{(McFrame == McFrameKind.Frame4E ? "4E" : "3E")}（{(McEncoding == McEncodingKind.Ascii ? "ASCII" : "二进制")}，{(Transport == SimulatorTransport.Udp ? "UDP" : "TCP")}）",
            SimulatedDevice.S7 => $"S7 ISO-on-TCP（PDU {S7Pdu}）",
            _ => "FINS/TCP",
        };
    }

    private void RefreshMemory()
    {
        WordRows.Clear();
        BitRows.Clear();
        ByteRows.Clear();
        WordArea? words = _memory.FindWords(SelectedArea);
        BitArea? bits = _memory.FindBits(SelectedArea);
        ByteArea? bytes = _memory.FindBytes(SelectedArea);
        IsWordArea = words is not null;
        IsBitArea = bits is not null;
        IsByteArea = bytes is not null;
        if (words is not null)
        {
            RefreshWords(words);
        }
        else if (bits is not null)
        {
            RefreshBits(bits);
        }
        else if (bytes is not null)
        {
            RefreshBytes(bytes);
        }
        else
        {
            MemoryPageText = "没有存储区";
        }
    }

    private void RefreshWords(WordArea area)
    {
        (int start, int count, int pages) = PageOf(area.Length, PageSize);
        ushort[] values = area.Read(start, count);
        for (int index = 0; index < values.Length; index++)
        {
            ushort value = values[index];
            WordRows.Add(new SimulatorMemoryRow(
                start + index,
                value,
                unchecked((short)value),
                $"0x{value:X4}",
                Convert.ToString(value, 2).PadLeft(16, '0')));
        }

        MemoryPageText = PageText(start, count, area.Length, "字", pages);
    }

    private void RefreshBits(BitArea area)
    {
        (int start, int count, int pages) = PageOf(area.Length, ElementsPerPage);
        bool[] values = area.Read(start, count);
        for (int offset = 0; offset < values.Length; offset += ElementsPerRow)
        {
            IEnumerable<string> cells = values[offset..Math.Min(offset + ElementsPerRow, values.Length)].Select(bit => bit ? "1" : "0");
            BitRows.Add(new SimulatorBitRow(start + offset, string.Join(' ', cells)));
        }

        MemoryPageText = PageText(start, count, area.Length, "位", pages);
    }

    private void RefreshBytes(ByteArea area)
    {
        (int start, int count, int pages) = PageOf(area.Length, ElementsPerPage);
        byte[] values = area.Read(start, count);
        for (int offset = 0; offset < values.Length; offset += ElementsPerRow)
        {
            ByteRows.Add(new SimulatorByteRow(start + offset, Hex(values[offset..Math.Min(offset + ElementsPerRow, values.Length)])));
        }

        MemoryPageText = PageText(start, count, area.Length, "字节", pages);
    }

    /// <summary>按每页的元素数算出当前页的起点与数量，并把页号限制在有效范围内。</summary>
    private (int Start, int Count, int Pages) PageOf(int length, int perPage)
    {
        int pages = Math.Max(1, (length + perPage - 1) / perPage);
        PageIndex = Math.Clamp(PageIndex, 0, pages - 1);
        int start = PageIndex * perPage;
        return (start, Math.Min(perPage, length - start), pages);
    }

    private string PageText(int start, int count, int length, string unit, int pages) =>
        $"{start} 至 {start + count - 1}（共 {length} 个{unit}，第 {PageIndex + 1}/{pages} 页）";

    private void ChangePage(int delta)
    {
        PageIndex += delta;
        RefreshMemory();
    }

    private void WriteWord()
    {
        WordArea? area = _memory.FindWords(SelectedArea);
        if (area is null)
        {
            return;
        }

        if (!TryParseWord(EditValue, out ushort value))
        {
            MemoryMessage = "写入的值必须是 0 到 0xFFFF 之间的十进制或 0x 开头的十六进制数。";
            return;
        }

        if (EditAddress < 0 || EditAddress >= area.Length)
        {
            MemoryMessage = $"地址必须在 0 到 {area.Length - 1} 之间。";
            return;
        }

        area.Write(EditAddress, [value]);
        MemoryMessage = string.Empty;
        RefreshMemory();
    }

    /// <summary>新建 S7 数据块 <c>DB{n}</c>；编号或大小不合法、编号已存在时只写说明，不改变内存。</summary>
    private void AddDataBlock()
    {
        if (!TryParseDecimal(NewDataBlockNumber, 1, MaxDataBlockNumber, out int number))
        {
            DataBlockMessage = $"数据块号必须在 1 到 {MaxDataBlockNumber} 之间。";
            return;
        }

        if (!TryParseDecimal(NewDataBlockSize, 1, MaxDataBlockSize, out int size))
        {
            DataBlockMessage = $"数据块的大小必须在 1 到 {MaxDataBlockSize} 字节之间。";
            return;
        }

        string name = $"DB{number}";
        if (_memory.FindBytes(name) is not null)
        {
            DataBlockMessage = $"{name} 已经存在。";
            return;
        }

        _memory.AddBytes(name, size);
        MemoryAreas.Add(name);
        SelectedArea = name;
        DataBlockMessage = $"已创建 {name}（{size} 字节）。";
    }

    private void ApplyFaults()
    {
        if (FaultDelayMilliseconds is < 0 or > MaxFaultDelayMilliseconds)
        {
            FaultMessage = $"应答延迟必须在 0 到 {MaxFaultDelayMilliseconds} 毫秒之间。";
            return;
        }

        try
        {
            int? forced = string.IsNullOrWhiteSpace(FaultForcedErrorText) ? null : ParseNumber(FaultForcedErrorText);
            _faults.Plan = new FaultPlan
            {
                DelayMilliseconds = FaultDelayMilliseconds,
                NoReply = FaultNoReply,
                ForcedErrorCode = forced,
                CorruptChecksum = FaultCorruptChecksum,
                DisconnectOnNextRequest = FaultDisconnectNext,
            };
            FaultMessage = "故障注入已设置。";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            FaultMessage = "强制错误码必须是 0 到 0xFFFF 之间的十进制或 0x 开头的十六进制数。";
        }
    }

    private void ClearFaults()
    {
        _faults.Plan = FaultPlan.None;
        FaultDelayMilliseconds = 0;
        FaultNoReply = false;
        FaultForcedErrorText = string.Empty;
        FaultCorruptChecksum = false;
        FaultDisconnectNext = false;
        FaultMessage = "故障注入已清除。";
    }

    private void AddRequest(SimulatorRequestEventArgs e)
    {
        var row = new SimulatorRequestRow(
            e.At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            e.RemoteEndPoint ?? "串口",
            e.Kind switch
            {
                SimulatorReplyKind.Reply => "应答",
                SimulatorReplyKind.NoReply => "不应答",
                _ => "断开",
            },
            e.Summary,
            Hex(e.Request),
            e.Response is null ? "—" : Hex(e.Response));
        Requests.InsertNewest(row, LogCapacity);
    }

    private async Task IncrementAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(AutoIncrementMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                WordArea? area = _memory.FindWords(SelectedArea);
                if (area is null)
                {
                    continue;
                }

                ushort[] values = area.Read(0, area.Length);
                for (int index = 0; index < values.Length; index++)
                {
                    values[index]++;
                }

                area.Write(0, values);
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭自增时正常结束。
        }
    }

    private static string Hex(byte[] data) => string.Join(' ', data.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static bool TryParseWord(string text, out ushort value)
    {
        try
        {
            value = checked((ushort)ParseNumber(text));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            value = 0;
            return false;
        }
    }

    private static bool TryParseDecimal(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    private static int ParseNumber(string text)
    {
        string trimmed = text.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(trimmed[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : int.Parse(trimmed, CultureInfo.InvariantCulture);
    }
}
