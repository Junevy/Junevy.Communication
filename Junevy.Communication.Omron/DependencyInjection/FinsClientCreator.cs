using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Omron.DependencyInjection;

/// <summary>
/// 按 <see cref="FinsClientConfig"/> 创建 FINS 客户端的创建器。由 <see cref="OmronServiceCollectionExtensions.AddOmronFins"/> 注册，
/// 由 <see cref="Junevy.Communication.Plc.PlcClientFactory"/> 按配置的精确类型选中。
/// TCP 是唯一的传输（P4-D2），分帧与应答关联由 <c>FinsClient.Create</c> 内部设定，配置里没有可选项。
/// </summary>
public sealed class FinsClientCreator : IPlcClientCreator
{
    /// <inheritdoc />
    public Type ConfigType => typeof(FinsClientConfig);

    /// <inheritdoc />
    public IPlcClient Create(string name, IPlcClientConfig config, ILoggerFactory loggerFactory)
    {
        if (config is not FinsClientConfig fins)
            throw new ArgumentException($"Expected {nameof(FinsClientConfig)} but received {config.GetType().FullName}.", nameof(config));

        TcpClientChannelConfig transport = fins.Tcp
            ?? throw new ArgumentException($"{nameof(FinsClientConfig)}.{nameof(FinsClientConfig.Tcp)} is required because FINS runs over FINS/TCP only (P4-D2).",
                                           nameof(config));

        return FinsClient.Create(name, transport, FinsClient.BuiltInCreators.Resolve(transport), fins.Options, loggerFactory);
    }
}
