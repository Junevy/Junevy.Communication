using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Udp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// UDP 页（计划第 7.13 节）：定向（固定远端）或非定向（只绑定本地端口，每次指定地址）。
/// 数据报没有分帧，因此没有分帧参数。
/// </summary>
public sealed partial class UdpSessionViewModel : ChannelSessionViewModel
{
    public UdpSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies, new ChannelSettingsEditor(ChannelTransport.Udp))
    {
    }

    [ObservableProperty]
    private string _localAddress = "0.0.0.0";

    /// <summary>本地端口，0 表示由系统选择。</summary>
    [ObservableProperty]
    private int _localPort;

    /// <summary>定向：发送到固定的远端；非定向：只绑定本地端口。</summary>
    [ObservableProperty]
    private bool _directed = true;

    [ObservableProperty]
    private string _remoteHost = "127.0.0.1";

    [ObservableProperty]
    private int _remotePort = 4000;

    /// <summary>非定向模式下发送的目标地址。</summary>
    [ObservableProperty]
    private string _targetHost = "127.0.0.1";

    [ObservableProperty]
    private int _targetPort = 4000;

    [ObservableProperty]
    private bool _enableBroadcast;

    /// <summary>组播地址，逗号分隔；留空表示不加入组播。</summary>
    [ObservableProperty]
    private string _multicastGroups = string.Empty;

    [ObservableProperty]
    private int _multicastTimeToLive = 1;

    [ObservableProperty]
    private bool _multicastLoopback;

    [ObservableProperty]
    private bool _reuseAddress;

    /// <summary>请求超时后的重试次数（每次尝试都有自己的超时）。</summary>
    [ObservableProperty]
    private int _requestRetryCount;

    /// <summary>数据报长度上限（字节），范围由库检查。</summary>
    [ObservableProperty]
    private int _maxDatagramSize = 65507;

    public override string Target => Directed ? $"{RemoteHost}:{RemotePort}" : $"{TargetHost}:{TargetPort}";

    protected override IClientChannel CreateChannel() => new UdpChannel(
        BuildConfig(),
        new Logger<UdpChannel>(SessionLoggerFactory),
        Settings.ToComponents<ChannelComponents>());

    /// <summary>非定向模式使用 <c>SendToAsync</c>，定向模式使用通道的远端。</summary>
    protected override Task<CommResult> SendCoreAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (Directed || CurrentChannel is not UdpChannel udp)
        {
            return base.SendCoreAsync(payload, cancellationToken);
        }

        return TryTarget(out IPEndPoint? target, out CommResult? failure)
            ? udp.SendToAsync(target!, payload, cancellationToken)
            : Task.FromResult(failure!);
    }

    protected override Task<CommResult<byte[]>> RequestCoreAsync(byte[] payload, RequestOptions options, CancellationToken cancellationToken)
    {
        if (Directed || CurrentChannel is not UdpChannel udp)
        {
            return base.RequestCoreAsync(payload, options, cancellationToken);
        }

        if (!TryTarget(out IPEndPoint? target, out CommResult? failure))
        {
            return Task.FromResult(CommResult<byte[]>.Fail(failure!.ErrorMessage ?? "Invalid target.", failure.ErrorKind));
        }

        return udp.RequestToAsync(target!, payload, options, cancellationToken);
    }

    public override SessionSettings CaptureSettings() => new UdpSettings(
        LocalAddress,
        LocalPort,
        Directed,
        RemoteHost,
        RemotePort,
        TargetHost,
        TargetPort,
        EnableBroadcast,
        MulticastGroups,
        MulticastTimeToLive,
        MulticastLoopback,
        ReuseAddress,
        RequestRetryCount,
        MaxDatagramSize,
        Settings.Capture());

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not UdpSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a UDP page.", nameof(settings));
        }

        LocalAddress = saved.LocalAddress;
        LocalPort = saved.LocalPort;
        Directed = saved.Directed;
        RemoteHost = saved.RemoteHost;
        RemotePort = saved.RemotePort;
        TargetHost = saved.TargetHost;
        TargetPort = saved.TargetPort;
        EnableBroadcast = saved.EnableBroadcast;
        MulticastGroups = saved.MulticastGroups;
        MulticastTimeToLive = saved.MulticastTimeToLive;
        MulticastLoopback = saved.MulticastLoopback;
        ReuseAddress = saved.ReuseAddress;
        RequestRetryCount = saved.RequestRetryCount;
        MaxDatagramSize = saved.MaxDatagramSize;
        Settings.Apply(saved.Channel);
    }

    private bool TryTarget(out IPEndPoint? target, out CommResult? failure)
    {
        if (!IPAddress.TryParse(TargetHost, out IPAddress? address))
        {
            target = null;
            failure = CommResult.Fail($"Invalid target address '{TargetHost}'.", CommErrorKind.InvalidRequest);
            return false;
        }

        target = new IPEndPoint(address, TargetPort);
        failure = null;
        return true;
    }

    private UdpChannelConfig BuildConfig()
    {
        string[] groups = MulticastGroups
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ChannelConfigs.Udp(
            Settings, LocalAddress, LocalPort, Directed ? RemoteHost : null, RemotePort, EnableBroadcast, groups,
            MulticastTimeToLive, MulticastLoopback, ReuseAddress, RequestRetryCount, MaxDatagramSize);
    }
}
