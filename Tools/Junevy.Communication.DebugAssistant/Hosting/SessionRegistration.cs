using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Fins;
using Junevy.Communication.DebugAssistant.Protocols.Ftp;
using Junevy.Communication.DebugAssistant.Protocols.Melsec;
using Junevy.Communication.DebugAssistant.Protocols.Modbus;
using Junevy.Communication.DebugAssistant.Protocols.Mqtt;
using Junevy.Communication.DebugAssistant.Protocols.OpcUa;
using Junevy.Communication.DebugAssistant.Protocols.Simulators;
using Junevy.Communication.DebugAssistant.Protocols.Secs;
using Junevy.Communication.DebugAssistant.Protocols.Siemens;
using Junevy.Communication.DebugAssistant.Protocols.WebApi;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Hosting;

/// <summary>页签模块的 DI 注册：工厂、命名器、共享依赖，以及每种页签的键控服务（DA-D19）。</summary>
internal static class SessionRegistration
{
    /// <summary>每种协议的页签工厂（DA-D19）：新增一种协议时在这里加一行。</summary>
    private static readonly Dictionary<ProtocolKind, Func<ProtocolDescriptor, SessionServices, SessionViewModel>> Pages = new()
    {
        [ProtocolKind.TcpClient] = (descriptor, services) => new TcpClientSessionViewModel(descriptor, services),
        [ProtocolKind.Udp] = (descriptor, services) => new UdpSessionViewModel(descriptor, services),
        [ProtocolKind.Modbus] = (descriptor, services) => new ModbusSessionViewModel(descriptor, services),
        [ProtocolKind.Mc] = (descriptor, services) => new McSessionViewModel(descriptor, services),
        [ProtocolKind.S7] = (descriptor, services) => new S7SessionViewModel(descriptor, services),
        [ProtocolKind.Fins] = (descriptor, services) => new FinsSessionViewModel(descriptor, services),
        [ProtocolKind.ModbusSlave] = (descriptor, services) => new SimulatorSessionViewModel(descriptor, services),
        [ProtocolKind.McSlave] = (descriptor, services) => new SimulatorSessionViewModel(descriptor, services),
        [ProtocolKind.S7Cpu] = (descriptor, services) => new SimulatorSessionViewModel(descriptor, services),
        [ProtocolKind.FinsSlave] = (descriptor, services) => new SimulatorSessionViewModel(descriptor, services),
        [ProtocolKind.TcpServer] = (descriptor, services) => new TcpServerSessionViewModel(descriptor, services),
        [ProtocolKind.Secs] = (descriptor, services) => new SecsSessionViewModel(descriptor, services),
        [ProtocolKind.RestClient] = (descriptor, services) => new RestClientSessionViewModel(descriptor, services),
        [ProtocolKind.SoapClient] = (descriptor, services) => new SoapClientSessionViewModel(descriptor, services),
        [ProtocolKind.HttpServer] = (descriptor, services) => new WebApiServerSessionViewModel(descriptor, services),
        [ProtocolKind.Mqtt] = (descriptor, services) => new MqttSessionViewModel(descriptor, services),
        [ProtocolKind.OpcUa] = (descriptor, services) => new OpcUaSessionViewModel(descriptor, services),
        [ProtocolKind.Serial] = (descriptor, services) => new SerialSessionViewModel(descriptor, services),
        [ProtocolKind.Ftp] = (descriptor, services) => new FtpSessionViewModel(descriptor, services),
    };

    internal static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddSingleton<SessionNamer>();
        services.AddSingleton<ISessionFactory, SessionFactory>();

        foreach (ProtocolDescriptor descriptor in ProtocolCatalog.All)
        {
            // 每个协议目录都必须有页签工厂：缺少时在这里直接失败，而不是静默退回占位页签。
            Func<ProtocolDescriptor, SessionServices, SessionViewModel> create = Pages[descriptor.Kind];
            services.AddKeyedTransient<SessionViewModel>(
                descriptor.Kind,
                (provider, _) => create(descriptor, provider.GetRequiredService<SessionServices>()));
        }

        return services;
    }
}
