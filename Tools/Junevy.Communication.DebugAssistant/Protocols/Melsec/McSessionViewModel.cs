using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Melsec;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Melsec;

/// <summary>
/// 三菱 MC 页签（计划第 7.3 节）：帧类型、编码与传输的组合，和校验，访问路径，监视定时器与软元件速查表；
/// 读写与监控使用公共面板。
/// </summary>
public sealed partial class McSessionViewModel : PlcSessionViewModel
{
    public McSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies, new ChannelSettingsEditor(ChannelTransport.Tcp, protocolOwnsChannelFraming: true), WordOrder.DCBA)
    {
        UseDeviceCommand = new RelayCommand<McDeviceEntry?>(UseDevice);

        // 请求超时改变时，监视定时器的警告随之更新。
        Channel.Timeouts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimeoutEditor.RequestTimeout))
            {
                RefreshTimer();
            }
        };
        RefreshFrames();
        RefreshCombination();
        RefreshTimer();
    }

    /// <summary>软元件速查表（28 项）。</summary>
    public IReadOnlyList<McDeviceEntry> Devices => McDevices.All;

    /// <summary>当前传输允许的帧类型；不合法的帧不在列表中，因此不可选。</summary>
    public ObservableCollection<McFrameType> AvailableFrames { get; } = [];

    public IRelayCommand<McDeviceEntry?> UseDeviceCommand { get; }

    [ObservableProperty]
    private PlcTransport _transport = PlcTransport.Tcp;

    [ObservableProperty]
    private McFrameType _frame = McFrameType.Frame3E;

    [ObservableProperty]
    private McEncoding _encoding = McEncoding.Binary;

    /// <summary>组合的名称，或不合法的原因。</summary>
    [ObservableProperty]
    private string _combinationName = string.Empty;

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 5001;

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

    /// <summary>和校验，只在串口（4C）时可编辑；库的默认值为开启。</summary>
    [ObservableProperty]
    private bool _sumCheck = true;

    /// <summary>访问路径的网络号（十六进制文字，0 到 FF）。</summary>
    [ObservableProperty]
    private string _networkNumberHex = "00";

    /// <summary>访问路径的 PC 号（十六进制文字，0 到 FF）。</summary>
    [ObservableProperty]
    private string _pcNumberHex = "FF";

    /// <summary>访问路径的请求目标模块 I/O 号（十六进制文字，0 到 FFFF）。</summary>
    [ObservableProperty]
    private string _moduleIoNumberHex = "03FF";

    /// <summary>访问路径的请求目标模块站号（十六进制文字，0 到 FF）。</summary>
    [ObservableProperty]
    private string _moduleStationNumberHex = "00";

    /// <summary>4C 的站号（十六进制文字），只在串口时使用。</summary>
    [ObservableProperty]
    private string _stationNumberHex = "00";

    /// <summary>4C 的本站号（十六进制文字），只在串口时使用。</summary>
    [ObservableProperty]
    private string _selfStationNumberHex = "00";

    /// <summary>监视定时器的值（单位 250 毫秒），1 到 65535。</summary>
    [ObservableProperty]
    private int _monitoringTimerUnits = 4;

    /// <summary>折算的毫秒数，例如 "1000 毫秒"。</summary>
    [ObservableProperty]
    private string _monitoringTimerText = string.Empty;

    /// <summary>监视定时器不小于请求超时时的警告；否则为空。</summary>
    [ObservableProperty]
    private string _monitoringTimerWarning = string.Empty;

    public bool IsSerial => Transport == PlcTransport.Serial;

    /// <summary>
    /// 传输改变时：通道参数视图随之切换（套接字与 TLS 只属于 TCP）；帧类型列表随之更新，
    /// 当前帧不合法时改为该传输的第一个合法帧。
    /// </summary>
    partial void OnTransportChanged(PlcTransport value)
    {
        Channel.Transport = value switch
        {
            PlcTransport.Udp => ChannelTransport.Udp,
            PlcTransport.Serial => ChannelTransport.Serial,
            _ => ChannelTransport.Tcp,
        };
        RefreshFrames();
        if (McCombination.Problem(value, Frame) is not null)
        {
            Frame = McCombination.FramesFor(value)[0];
        }

        RefreshCombination();
        OnPropertyChanged(nameof(IsSerial));
    }

    partial void OnFrameChanged(McFrameType value) => RefreshCombination();

    partial void OnEncodingChanged(McEncoding value) => RefreshCombination();

    partial void OnMonitoringTimerUnitsChanged(int value) => RefreshTimer();

    /// <summary>
    /// 按传输与帧创建 MC 客户端：访问路径、监视定时器与和校验来自界面；分帧与关联由协议决定。
    /// 组合或数值不合法时抛出 <see cref="ArgumentException"/>，不创建客户端。
    /// </summary>
    protected override IPlcClient CreateClient()
    {
        if (McCombination.Problem(Transport, Frame) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        if (MonitoringTimerUnits is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException("监视定时器必须在 1 到 65535 之间（单位 250 毫秒）。");
        }

        return McClient.Create(BuildTransportConfig(), Dependencies.Transports.Get(Transport), BuildOptions(), SessionLoggerFactory);
    }

    /// <summary>
    /// 把界面的数值写入客户端选项。4C 的站号与本站号只在串口时读取：网络帧不发送它们，因此它们的文字不参与校验。
    /// 文字不合法时抛出 <see cref="ArgumentException"/>。
    /// </summary>
    private McClientOptions BuildOptions()
    {
        try
        {
            var options = new McClientOptions
            {
                FrameType = Frame,
                Encoding = Encoding,
                SumCheck = SumCheck,
                NetworkNumber = McAccessPath.ParseByte(NetworkNumberHex),
                PcNumber = McAccessPath.ParseByte(PcNumberHex),
                ModuleIoNumber = McAccessPath.ParseWord(ModuleIoNumberHex),
                ModuleStationNumber = McAccessPath.ParseByte(ModuleStationNumberHex),
                StationNumber = IsSerial ? McAccessPath.ParseByte(StationNumberHex) : (byte)0,
                SelfStationNumber = IsSerial ? McAccessPath.ParseByte(SelfStationNumberHex) : (byte)0,
                MonitoringTimer = (ushort)MonitoringTimerUnits,
                DataFormat = Options.DataFormatOverride(),
            };
            Options.ApplyTo(options);
            return options;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    }

    public override SessionSettings CaptureSettings() => new McSettings(
        CapturePanel(),
        Transport,
        Frame,
        Encoding,
        SumCheck,
        Host,
        Port,
        PortName,
        BaudRate,
        DataBits,
        Parity,
        StopBits,
        NetworkNumberHex,
        PcNumberHex,
        ModuleIoNumberHex,
        ModuleStationNumberHex,
        StationNumberHex,
        SelfStationNumberHex,
        MonitoringTimerUnits);

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not McSettings saved)
        {
            throw new ArgumentException("The settings do not belong to an MC page.", nameof(settings));
        }

        Transport = saved.Transport;
        Frame = saved.Frame;
        Encoding = saved.Encoding;
        SumCheck = saved.SumCheck;
        Host = saved.Host;
        Port = saved.Port;
        PortName = saved.PortName;
        BaudRate = saved.BaudRate;
        DataBits = saved.DataBits;
        Parity = saved.Parity;
        StopBits = saved.StopBits;
        NetworkNumberHex = saved.NetworkNumberHex;
        PcNumberHex = saved.PcNumberHex;
        ModuleIoNumberHex = saved.ModuleIoNumberHex;
        ModuleStationNumberHex = saved.ModuleStationNumberHex;
        StationNumberHex = saved.StationNumberHex;
        SelfStationNumberHex = saved.SelfStationNumberHex;
        MonitoringTimerUnits = saved.MonitoringTimerUnits;
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

    /// <summary>帧列表与传输一致；列表没有变化时不重建，避免选中项被清空。</summary>
    private void RefreshFrames()
    {
        IReadOnlyList<McFrameType> frames = McCombination.FramesFor(Transport);
        if (AvailableFrames.SequenceEqual(frames))
        {
            return;
        }

        AvailableFrames.Clear();
        foreach (McFrameType frame in frames)
        {
            AvailableFrames.Add(frame);
        }
    }

    private void RefreshCombination()
    {
        CombinationName = McCombination.Name(Transport, Frame, Encoding)
            ?? McCombination.Problem(Transport, Frame)
            ?? string.Empty;
    }

    private void RefreshTimer()
    {
        (long milliseconds, string? warning) = McTimers.Describe(MonitoringTimerUnits, Channel.Timeouts.RequestTimeout);
        MonitoringTimerText = $"{milliseconds} 毫秒";
        MonitoringTimerWarning = warning ?? string.Empty;
    }

    /// <summary>速查表的一行：把软元件的示例地址填入地址。</summary>
    private void UseDevice(McDeviceEntry? device)
    {
        if (device is not null)
        {
            Address = McDevices.SampleAddress(device);
        }
    }
}
