using Junevy.Communication.Channels;
using Junevy.Communication.Plc;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Melsec.DependencyInjection;

/// <summary>
/// 按 <see cref="McClientConfig"/> 创建 MELSEC 客户端的创建器。由 <see cref="MelsecServiceCollectionExtensions.AddMelsec"/> 注册，
/// 由 <see cref="Junevy.Communication.Plc.PlcClientFactory"/> 按配置的精确类型选中。
/// </summary>
public sealed class McClientCreator : IPlcClientCreator
{
    /// <inheritdoc />
    public Type ConfigType => typeof(McClientConfig);

    /// <inheritdoc />
    public IPlcClient Create(string name, IPlcClientConfig config, ILoggerFactory loggerFactory)
    {
        if (config is not McClientConfig mc)
            throw new ArgumentException($"Expected {nameof(McClientConfig)} but received {config.GetType().FullName}.", nameof(config));

        IChannelConfig transport = mc.Transport switch
        {
            McTransport.Tcp => Require(mc.Tcp, mc.Transport),
            McTransport.Udp => Require(mc.Udp, mc.Transport),
            McTransport.Serial => Require(mc.Serial, mc.Transport),
            _ => throw new ArgumentException($"Unknown MELSEC transport '{mc.Transport}'.", nameof(config)),
        };

        return McClient.Create(name, transport, McClient.BuiltInCreators.Resolve(transport), mc.Options, loggerFactory);
    }

    private static T Require<T>(T? transport, McTransport kind) where T : class
        => transport ?? throw new ArgumentException($"McClientConfig.{kind} is required when Transport is {kind}.", "config");
}
