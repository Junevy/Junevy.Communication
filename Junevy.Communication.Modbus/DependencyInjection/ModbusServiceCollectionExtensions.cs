using Junevy.Communication.Modbus.Core.Framing;
using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Parsing;
using Junevy.Communication.Modbus.Factory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.Modbus.DependencyInjection;

/// <summary>
/// Extension methods for registering Modbus services in the DI container.
/// </summary>
public static class ModbusServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IModbusFactory"/> and its dependencies as singletons.
    /// </summary>
    public static IServiceCollection AddModbusFactory(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Ensure a logger factory is available
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        // Ensure a logger is available so DI can select the ModbusFactory constructor
        // and inject the container-registered IModbusConnectionManager and creators
        services.TryAddSingleton<ILogger<ModbusFactory>>(sp =>
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModbusFactory>());

        // Register the PDU validator shared by both parsers
        services.TryAddSingleton<IModbusPduValidator, ModbusPduValidator>();
        services.TryAddSingleton<IModbusFrameBuilder, ModbusFrameBuilder>();

        // Register protocol-specific parsers
        services.TryAddSingleton<TcpProtocolParser>();
        services.TryAddSingleton<RtuProtocolParser>();

        // Register the connection manager for standalone use
        services.TryAddSingleton<IModbusConnectionManager, ModbusConnectionManager>();

        // Built-in client creation strategies. These use AddSingleton (not TryAdd) so a creator
        // registered by the caller AFTER AddModbusFactory() wins — last registration wins.
        services.AddSingleton<IModbusClientCreator>(sp => new TcpClientCreator(
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TcpProtocolParser>(),
            sp.GetRequiredService<IModbusFrameBuilder>()));
        services.AddSingleton<IModbusClientCreator>(sp => new RtuClientCreator(
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<RtuProtocolParser>(),
            sp.GetRequiredService<IModbusFrameBuilder>()));

        // Register the factory (receives creators and the manager via DI)
        services.TryAddSingleton<IModbusFactory, ModbusFactory>();

        return services;
    }
}
