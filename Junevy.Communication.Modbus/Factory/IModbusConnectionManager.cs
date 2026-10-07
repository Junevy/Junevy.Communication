using Junevy.Communication.Modbus.Core.Interfaces;
using Junevy.Communication.Modbus.Core.Models;

namespace Junevy.Communication.Modbus.Factory
{
    /// <summary>
    /// Manages the lifecycle of named Modbus instances.
    /// Supports aliasing so multiple logical names can share one physical connection
    /// (e.g., multiple RTU slaves on the same RS-485 bus).
    /// </summary>
    public interface IModbusConnectionManager : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Gets the total number of physical Modbus instances under management.
        /// </summary>
        int Count { get; }

        /// <summary>
        /// Gets all registered names (including aliases).
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
        /// Registers a new Modbus instance under the given name.
        /// Returns false if the name is already registered.
        /// </summary>
        bool Add(string key, IModbus modbus);

        /// <summary>
        /// Atomically retrieves an existing instance or adds the one produced by the factory.
        /// </summary>
        IModbus GetOrAdd(string key, Func<string, IModbus> factory);

        /// <summary>
        /// Removes the instance registered under the given name, every alias that resolves to it, and disposes the instance. Removing an alias only removes the alias.
        /// </summary>
        bool TryRemove(string key);

        /// <summary>
        /// Registers an alias so that <paramref name="aliasKey"/> resolves to the same
        /// instance as <paramref name="existingKey"/>. Useful for multi-drop RS-485 where
        /// multiple slaves share one physical connection.
        /// </summary>
        /// <returns>
        /// True if the alias was registered. Returns false when existingKey is not registered, when aliasKey equals existingKey, or when aliasKey already exists.
        /// </returns>
        bool RegisterAlias(string aliasKey, string existingKey);

        /// <summary>
        /// Removes an alias without disposing the underlying instance.
        /// </summary>
        bool RemoveAlias(string aliasKey);
    }
}
