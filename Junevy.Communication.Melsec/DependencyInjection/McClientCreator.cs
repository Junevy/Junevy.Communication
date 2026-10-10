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

        return mc.Transport switch
        {
            McTransport.Tcp => McClient.CreateTcp(name, Require(mc.Tcp, mc.Transport), mc.Options, loggerFactory),
            McTransport.Udp => McClient.CreateUdp(name, Require(mc.Udp, mc.Transport), mc.Options, loggerFactory),
            McTransport.Serial => McClient.CreateSerial(name, Require(mc.Serial, mc.Transport), mc.Options, loggerFactory),
            _ => throw new ArgumentException($"Unknown MELSEC transport '{mc.Transport}'.", nameof(config)),
        };
    }

    private static T Require<T>(T? transport, McTransport kind) where T : class
        => transport ?? throw new ArgumentException($"McClientConfig.{kind} is required when Transport is {kind}.", "config");
}
