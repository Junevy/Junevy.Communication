using Junevy.Communication.Channels;
using Junevy.Communication.Plc;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Modbus.DependencyInjection;

/// <summary>把 <see cref="ModbusClientConfig"/> 变成 Modbus 客户端的创建器（P7-D14）。</summary>
public sealed class ModbusClientCreator : IPlcClientCreator
{
    /// <inheritdoc />
    public Type ConfigType => typeof(ModbusClientConfig);

    /// <inheritdoc />
    public IPlcClient Create(string name, IPlcClientConfig config, ILoggerFactory loggerFactory)
    {
        if (config is not ModbusClientConfig modbus)
            throw new ArgumentException($"Expected {nameof(ModbusClientConfig)} but received {config.GetType().FullName}.", nameof(config));

        IChannelConfig transport = modbus.Transport switch
        {
            ModbusTransport.Tcp => Require(modbus.Tcp, modbus.Transport),
            ModbusTransport.Udp => Require(modbus.Udp, modbus.Transport),
            ModbusTransport.Serial => Require(modbus.Serial, modbus.Transport),
            _ => throw new ArgumentException($"Unknown Modbus transport '{modbus.Transport}'.", nameof(config)),
        };

        return ModbusClient.Create(name, transport, ModbusClient.BuiltInCreators.Resolve(transport), modbus.Options, loggerFactory);
    }

    private static T Require<T>(T? transport, ModbusTransport kind) where T : class
        => transport ?? throw new ArgumentException($"ModbusClientConfig.{kind} is required when Transport is {kind}.", "config");
}
