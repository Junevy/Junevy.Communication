using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Siemens.DependencyInjection;

/// <summary>
/// 按 <see cref="S7ClientConfig"/> 创建 S7 客户端的创建器。由 <see cref="SiemensServiceCollectionExtensions.AddSiemensS7"/> 注册，
/// 由 <see cref="Junevy.Communication.Plc.PlcClientFactory"/> 按配置的精确类型选中。
/// TCP 是唯一的传输（P4-D2），分帧与应答关联由 <c>S7Client.Create</c> 内部设定，配置里没有可选项。
/// </summary>
public sealed class S7ClientCreator : IPlcClientCreator
{
    /// <inheritdoc />
    public Type ConfigType => typeof(S7ClientConfig);

    /// <inheritdoc />
    public IPlcClient Create(string name, IPlcClientConfig config, ILoggerFactory loggerFactory)
    {
        if (config is not S7ClientConfig s7)
            throw new ArgumentException($"Expected {nameof(S7ClientConfig)} but received {config.GetType().FullName}.", nameof(config));

        TcpClientChannelConfig transport = s7.Tcp
            ?? throw new ArgumentException($"{nameof(S7ClientConfig)}.{nameof(S7ClientConfig.Tcp)} is required because S7 runs on ISO-on-TCP only (P4-D2).",
                                           nameof(config));

        return S7Client.Create(name, transport, S7Client.BuiltInCreators.Resolve(transport), s7.Options, loggerFactory);
    }
}
