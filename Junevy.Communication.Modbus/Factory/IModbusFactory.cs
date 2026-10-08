using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>
    /// Creates and manages named Modbus instances.
    /// Combines factory (creation) and registry (lifecycle) concerns behind a single facade.
    /// </summary>
    public interface IModbusFactory : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Total number of physical Modbus instances under management.
        /// </summary>
        int Count { get; }

        /// <summary>
        /// All registered names (including aliases).
        /// </summary>
        IEnumerable<string> Keys { get; }

        /// <summary>
        /// Retrieves a Modbus instance by name, or null if not found.
        /// </summary>
        IModbus? Get(string key);

        /// <summary>
        /// Retrieves a Modbus instance by name, cast to the specified type.
        /// Throws if not found or if the type does not match.
        /// </summary>
        TResult GetRequired<TResult>(string key) where TResult : class, IModbus;

        /// <summary>
        /// Attempts to retrieve a Modbus instance by name.
        /// </summary>
        bool TryGet(string key, out IModbus? modbus);

        /// <summary>
        /// Gets an existing instance or creates a new client. The <see cref="IModbusClientCreator"/>
        /// is resolved from the concrete type of <paramref name="config"/> (walking the base-type chain).
        /// Throws <see cref="NotSupportedException"/> when no creator is registered for that configuration type.
        /// </summary>
        IModbus GetOrAdd(string key, IModbusConfig config);

        /// <summary>
        /// Tries to add a new client. Fails if the key already exists (the newly created instance is
        /// disposed). The creator is resolved the same way as in <see cref="GetOrAdd(string, IModbusConfig)"/>.
        /// </summary>
        bool TryAdd(string key, IModbusConfig config, out IModbus? modbus);

        /// <summary>
        /// Removes the instance registered under the given name, every alias that resolves to it, and disposes the instance. Removing an alias only removes the alias.
        /// </summary>
        bool TryRemove(string key);

        /// <summary>
        /// Registers an alias so that <paramref name="aliasKey"/> resolves to the same
        /// instance as <paramref name="existingKey"/>. Useful for multi-drop RS-485 where
        /// multiple slave IDs share a single physical serial port connection.
        /// Returns false when existingKey is not registered, when aliasKey equals existingKey, or when aliasKey already exists.
        /// </summary>
        bool RegisterAlias(string aliasKey, string existingKey);

        /// <summary>
        /// Removes an alias without disposing the underlying instance.
        /// </summary>
        bool RemoveAlias(string aliasKey);
    }
}
