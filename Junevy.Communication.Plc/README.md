# Junevy.Communication.Plc

Address-based PLC client suite. It provides the common part of every protocol that reads and writes a device memory area by address: the client base class with request batching, retries and an operation lock, typed reads and writes, the address and option types, and the factory with dependency-injection registration. A protocol package (for example `Junevy.Communication.Melsec`) supplies only the protocol-specific parts. Connections, reconnect, heartbeat, timeouts and the typed API come from the channel family and this package.

> **Preview**: the package ships as `1.0.0-preview.1`. The API may change before the first stable release. Pin the version if you depend on it.

## Target Frameworks

`net472` and `net8.0`, the same as the channel family.

## Packages

| Package | Purpose | Depends on |
|---|---|---|
| `Junevy.Communication.Plc` | `IPlcClient`, `PlcClientBase<TAddress>`, typed extensions, addresses, options, `PlcClientFactory`, DI | `Junevy.Communication.Channels` |
| `Junevy.Communication.Melsec` | MELSEC MC protocol client (see its README) | Plc, Tcp, Udp, Serial |

Install a protocol package; it references this package. Preview packages need `--prerelease`:

```text
dotnet add package Junevy.Communication.Melsec --prerelease
```

## Typed Reads and Writes

Every protocol gets the same typed API as extension methods on `IPlcClient`. Reads return `CommResult<T>` (or `CommResult<T[]>` when a count is given); writes return `CommResult`. Each type has its own method name, so a literal never binds to the wrong width:

| Type | Read | Write |
|---|---|---|
| `short` / `ushort` | `ReadInt16Async` / `ReadUInt16Async` | `WriteInt16Async` / `WriteUInt16Async` |
| `int` / `uint` | `ReadInt32Async` / `ReadUInt32Async` | `WriteInt32Async` / `WriteUInt32Async` |
| `long` / `ulong` | `ReadInt64Async` / `ReadUInt64Async` | `WriteInt64Async` / `WriteUInt64Async` |
| `float` / `double` | `ReadFloatAsync` / `ReadDoubleAsync` | `WriteFloatAsync` / `WriteDoubleAsync` |
| `string` | `ReadStringAsync(address, byteLength)` | `WriteStringAsync` |
| `bool` | `ReadBoolAsync` | `WriteBoolAsync` |

The write methods have a single-value and an array overload. Multi-word values use the word order of the protocol's data format (`PlcDataFormat`); the default is `DCBA` with ASCII strings. A protocol decides its data format, so there is no per-call override.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;

public static class PlcTypedExample
{
    public static async Task<short> ReadSpeedAsync(IPlcClient plc, CancellationToken cancellationToken = default)
    {
        CommResult<short> speed = await plc.ReadInt16Async("D100", cancellationToken);
        if (!speed.IsSuccess)
            throw new InvalidOperationException($"D100 could not be read: {speed.ErrorKind}. {speed.ErrorMessage}");

        return speed.Data;
    }

    public static async Task WriteSetpointAsync(IPlcClient plc, float setpoint, CancellationToken cancellationToken = default)
    {
        CommResult result = await plc.WriteFloatAsync("D200", setpoint, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"D200 could not be written: {result.ErrorKind}. {result.ErrorMessage}");
    }
}
```

Raw bytes are available through `ReadAsync` and `WriteAsync`. Their layout is the protocol's normalised layout (for MELSEC: two bytes per word, little-endian, words in address order), so the same typed conversions apply to every encoding the protocol supports.

## Results, Not Exceptions

Operations do not throw for invalid input, device errors, timeouts or disconnects. They return a `CommResult` whose `ErrorKind` tells you what happened:

| `ErrorKind` | Meaning |
|---|---|
| `InvalidRequest` | Address cannot be parsed, a point count is out of range, or the protocol cannot represent the request |
| `NotConnected` | The channel is not connected when the operation starts, or the client has been disposed |
| `RemoteError` | The PLC answered with an error code; `ProtocolErrorCode` carries the raw code |
| `Timeout`, `ConnectionClosed`, `ProtocolViolation` | Transport or protocol failure; retried only if `RetryCount > 0` (see below) |
| `NotSupported` | The protocol or the options do not allow the operation, for example a bit write into a word device |
| `Cancelled` | The caller's cancellation token was signalled |

Only construction errors (invalid configuration) throw `ArgumentException`.

## Options and Retries

`PlcClientOptions` is the base of every protocol's options:

| Property | Default | Meaning |
|---|---|---|
| `RetryCount` | `0` | Retries after a retryable failure. The default fails fast, because a polling loop already retries |
| `RetryInterval` | `1000` ms | How long to wait for the link to come back before a retry |
| `AllowReadModifyWriteBits` | `false` | Allows writing one bit of a word device by reading and writing the word, under the client's operation lock. Each such write logs a warning |

Retries apply to each request separately: when a large read is split into packets, only the packet that failed is retried. A retry happens only when the link is restored within `RetryInterval`; otherwise the real failure is returned.

## Ownership

The client owns its channel. `Dispose` and `DisposeAsync` release the channel, and every later operation returns `NotConnected` with the message `The PLC client has been disposed.`

## Factory and Dependency Injection

`PlcClientFactory` creates and keeps named clients. It selects the creator by the exact runtime type of the configuration object, and throws `NotSupportedException` when no creator is registered for that type. `AddPlcClients` registers the factory as a singleton; each protocol package adds its creator to the same container.

```csharp
using Junevy.Communication.Plc;
using Junevy.Communication.Plc.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

public static class PlcDependencyInjectionExample
{
    public static IServiceCollection AddPlc(IServiceCollection services)
    {
        // Registers the PlcClientFactory singleton. Protocol packages add their creators to the same container.
        return services.AddPlcClients();
    }

    public static IPlcClient GetClient(PlcClientFactory factory, IPlcClientConfig config)
        => factory.GetOrAdd("line-1", config);
}
```

Calling `GetOrAdd` with a name that already exists returns the existing client and ignores the configuration you pass.

## Writing a Protocol

A protocol derives from `PlcClientBase<TAddress>` and implements the hooks that describe its frames: address parsing, point limits per request, the word and bit request and response builders, and the write response. It may override `ValidateRange` to reject requests the protocol cannot represent. The base class provides the rest. See `Junevy.Communication.Melsec` as the reference implementation, and the `using-junevy-plc` skill in the repository for a step-by-step guide.

A protocol offers one creation entry, which takes the transport configuration and a creator for that transport (the built-in creators are used when the protocol offers a shortcut). `PlcChannelBinding.CreateChannel` creates the channel: it takes the creator, the optional name, the transport configuration (its type must be the creator's `ConfigType`) and a delegate that returns the components for the creator's `Delivery` and `Medium`. A transport that is not built in supplies its own `IChannelCreator`, and the protocol works with it without changes to the protocol package.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Plc;
using Microsoft.Extensions.Logging;

public static class ProtocolChannelExample
{
    public static IClientChannel CreateChannel(IChannelCreator creator, IChannelConfig transport, ILoggerFactory? loggerFactory = null)
        => PlcChannelBinding.CreateChannel(
            creator,
            null,
            transport,
            (delivery, medium) => new ChannelComponents(),
            loggerFactory);
}
```
