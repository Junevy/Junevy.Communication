using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Serial;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// 串口页（计划第 7.13 节）：端口列表可刷新，不预选端口，也不自动打开；分帧默认为空闲间隔（USB 转串口的延迟需要它）。
/// </summary>
public sealed partial class SerialSessionViewModel : ChannelSessionViewModel
{
    public SerialSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies, new ChannelSettingsEditor(ChannelTransport.Serial))
    {
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        RefreshPorts();
    }

    /// <summary>当前系统中的串口名称（COM1、COM20 等）。</summary>
    public ObservableCollection<string> AvailablePorts { get; } = [];

    public IRelayCommand RefreshPortsCommand { get; }

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
    private Handshake _handshake = Handshake.None;

    [ObservableProperty]
    private bool _dtrEnable;

    [ObservableProperty]
    private bool _rtsEnable;

    [ObservableProperty]
    private int _readBufferSize = 4096;

    [ObservableProperty]
    private int _writeBufferSize = 2048;

    [ObservableProperty]
    private int _openTimeout = 2000;

    public override string Target => PortName;

    protected override IClientChannel CreateChannel() => new SerialChannel(
        BuildConfig(),
        new Logger<SerialChannel>(SessionLoggerFactory),
        Settings.ToComponents<ChannelComponents>());

    public override SessionSettings CaptureSettings() => new SerialSettings(
        PortName,
        BaudRate,
        DataBits,
        Parity,
        StopBits,
        Handshake,
        DtrEnable,
        RtsEnable,
        ReadBufferSize,
        WriteBufferSize,
        OpenTimeout,
        Settings.Capture());

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not SerialSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a serial page.", nameof(settings));
        }

        PortName = saved.PortName;
        BaudRate = saved.BaudRate;
        DataBits = saved.DataBits;
        Parity = saved.Parity;
        StopBits = saved.StopBits;
        Handshake = saved.Handshake;
        DtrEnable = saved.DtrEnable;
        RtsEnable = saved.RtsEnable;
        ReadBufferSize = saved.ReadBufferSize;
        WriteBufferSize = saved.WriteBufferSize;
        OpenTimeout = saved.OpenTimeout;
        Settings.Apply(saved.Channel);
    }

    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (string name in SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            AvailablePorts.Add(name);
        }
    }

    private SerialChannelConfig BuildConfig() => ChannelConfigs.Serial(
        Settings, PortName, BaudRate, DataBits, Parity, StopBits, Handshake, DtrEnable, RtsEnable, ReadBufferSize, WriteBufferSize, OpenTimeout);
}
