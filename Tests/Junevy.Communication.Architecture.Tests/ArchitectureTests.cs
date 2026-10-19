using System.Reflection;
using System.Runtime.CompilerServices;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec;
using Junevy.Communication.Modbus;
using Junevy.Communication.Omron;
using Junevy.Communication.Plc;
using Junevy.Communication.Secs;
using Junevy.Communication.Serial;
using Junevy.Communication.Siemens;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;
using Junevy.Communication.WebApi;

namespace Junevy.Communication.Architecture.Tests;

/// <summary>
/// 程序集分层与 InternalsVisibleTo 的守卫（P25-D2、计划 Task 1）。用反射读取程序集元数据，不解析 csproj 文本。
/// 编译器只为代码实际使用的类型写入引用，因此只在 csproj 中声明、代码中未使用的 ProjectReference 检查不到。
/// </summary>
public sealed class ArchitectureTests
{
    private const string CoreName = "Junevy.Communication.Core";
    private const string ChannelsName = "Junevy.Communication.Channels";
    private const string TcpName = "Junevy.Communication.Tcp";
    private const string UdpName = "Junevy.Communication.Udp";
    private const string SerialName = "Junevy.Communication.Serial";
    private const string PlcName = "Junevy.Communication.Plc";
    private const string MqttName = "Junevy.Communication.Mqtt";
    private const string OpcUaName = "Junevy.Communication.OpcUa";
    private const string FtpName = "Junevy.Communication.Ftp";
    private const string WebApiName = "Junevy.Communication.WebApi";
    private const string WebApiServerName = "Junevy.Communication.WebApi.Server";
    private const string LibraryPrefix = "Junevy.Communication.";
    private const string TestsSuffix = ".Tests";

    // 生产 → 生产的 InternalsVisibleTo 登记表（P25-D2）。理由照抄 Channels.csproj 中的注释。
    private static readonly (string Owner, string Friend)[] RegisteredGrants =
    {
        // Tcp 服务端会话复用字节流内部件（StreamChannel、FrameRouter、PendingRequestTable、HeartbeatMonitor），与客户端共用同一套分帧、关联与派发实现。
        (ChannelsName, TcpName),
        // Udp 的数据报驱动与 UdpChannel 复用 ConnectionSupervisor、DatagramChannel 等内部件（与 Tcp 的先例一致，计划 15.1）。
        (ChannelsName, UdpName),
    };

    // 分层表（计划 Task 1）：每个生产程序集允许引用的 Junevy.Communication.* 程序集。Plc 不得引用任何传输包；WebApi 只允许引用 Core（P3-D2）。
    private static readonly (Assembly Assembly, string[] AllowedReferences)[] LayerTable =
    {
        (typeof(CommResult).Assembly, Array.Empty<string>()),
        (typeof(StreamClientChannel).Assembly, new[] { CoreName }),
        (typeof(TcpServer).Assembly, new[] { ChannelsName, CoreName }),
        (typeof(UdpChannel).Assembly, new[] { ChannelsName, CoreName }),
        (typeof(SerialChannel).Assembly, new[] { ChannelsName, CoreName }),
        (typeof(PlcClientBase<>).Assembly, new[] { ChannelsName, CoreName }),
        (typeof(McClient).Assembly, new[] { PlcName, ChannelsName, CoreName, TcpName, UdpName, SerialName }),
        // Siemens 只有 ISO-on-TCP（端口 102），因此允许表里没有 Udp 与 Serial（P4-D2）。
        (typeof(S7Address).Assembly, new[] { PlcName, ChannelsName, CoreName, TcpName }),
        // Omron 同样只有 FINS/TCP（端口 9600），因此允许表里没有 Udp 与 Serial（P4-D1）。
        (typeof(FinsAddress).Assembly, new[] { PlcName, ChannelsName, CoreName, TcpName }),
        // Modbus 是 Plc 套件上的第四个协议包（P7-D1）：可以引用全部传输包，因为它支持 TCP、UDP 与串口。
        (typeof(ModbusDevice).Assembly, new[] { PlcName, ChannelsName, CoreName, TcpName, UdpName, SerialName }),
        (typeof(WebApiResult<>).Assembly, new[] { CoreName }),
        // Secs 是自有模型协议，不走 Plc 套件，但与它们一样直接坐在 Channels 与 Tcp 之上（P51-D1、D2）。不做 SECS-I，因此没有 Serial。
        (typeof(SecsMessage).Assembly, new[] { ChannelsName, CoreName, TcpName }),
#if NET8_0_OR_GREATER
        // SDK 适配包只面向 .NET 8（P6-C1），因此只在 net8.0 目标上参与检查；它不经过通道族，只允许引用 Core（P6-C6；服务端的例外见下一行）。
        (Assembly.Load(MqttName), new[] { CoreName }),
        (Assembly.Load(OpcUaName), new[] { CoreName }),
        (Assembly.Load(FtpName), new[] { CoreName }),
        // 服务端（P3.2）是 ASP.NET Core 的适配包，同时依赖 WebApi 的公共约定（P32-D2）；ASP.NET Core 只允许出现在 Hosting 命名空间（SdkAdapterArchitectureTests）。
        (Assembly.Load(WebApiServerName), new[] { CoreName, WebApiName }),
#endif
    };

    // 第三方程序集白名单（P6-C6）：分层表只管 Junevy.Communication.* 的方向，第三方由这里把关。
    // 未登记的程序集只允许 BCL 与微软一方的扩展包（System.*、Microsoft.*、netstandard、mscorlib）；登记在册的只有该包的 SDK 前缀。
    private static readonly Dictionary<string, string[]> SdkPrefixes = new Dictionary<string, string[]>
    {
#if NET8_0_OR_GREATER
        [MqttName] = new[] { "MQTTnet" },
        [OpcUaName] = new[] { "Opc.Ua" },
        [FtpName] = new[] { "FluentFTP" },
        [WebApiServerName] = new[] { "Microsoft.AspNetCore" },
#endif
    };

    // 参与 InternalsVisibleTo 检查的程序集：分层表中的全部程序集。
    private static readonly Assembly[] ProductionAssemblies = LayerTable.Select(row => row.Assembly).ToArray();

    [Fact]
    public void ProductionAssemblies_GrantInternalsOnlyToTheirTestsAndTheRegisteredExceptions()
    {
        var violations = new List<string>();
        foreach (Assembly assembly in ProductionAssemblies)
        {
            string owner = assembly.GetName().Name!;
            foreach (InternalsVisibleToAttribute grant in assembly.GetCustomAttributes<InternalsVisibleToAttribute>())
            {
                string friend = new AssemblyName(grant.AssemblyName).Name!;
                if (friend != owner + TestsSuffix && !RegisteredGrants.Contains((owner, friend)))
                    violations.Add($"{owner} -> {friend}");
            }
        }

        Assert.True(violations.Count == 0, $"Unregistered InternalsVisibleTo grants (owner -> friend): {string.Join(", ", violations)}");
    }

    [Fact]
    public void ProductionAssemblies_ReferenceOnlyLowerLayers()
    {
        var violations = new List<string>();
        foreach ((Assembly assembly, string[] allowed) in LayerTable)
        {
            string owner = assembly.GetName().Name!;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string name = reference.Name!;
                if (name.StartsWith(LibraryPrefix, StringComparison.Ordinal) && !allowed.Contains(name))
                    violations.Add($"{owner} -> {name}");
            }
        }

        Assert.True(violations.Count == 0, $"References outside the layering table (assembly -> referenced): {string.Join(", ", violations)}");
    }

    [Fact]
    public void ProductionAssemblies_ReferenceOnlyAllowlistedThirdPartyAssemblies()
    {
        var violations = new List<string>();
        foreach ((Assembly assembly, string[] libraries) in LayerTable)
        {
            string owner = assembly.GetName().Name!;
            string[] sdkPrefixes = SdkPrefixes.TryGetValue(owner, out string[]? registered) ? registered : Array.Empty<string>();
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string name = reference.Name!;
                bool allowlisted = libraries.Contains(name)
                    || sdkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
                    || IsBaseRuntimeAssembly(name);
                if (!allowlisted)
                {
                    violations.Add($"{owner} -> {name}");
                }
            }
        }

        Assert.True(violations.Count == 0, $"References outside the allow-list (assembly -> referenced): {string.Join(", ", violations)}");
    }

    private static bool IsBaseRuntimeAssembly(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || name == "System"
        || name == "netstandard"
        || name == "mscorlib";
}
