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
| `Junevy.Communication.Siemens` | Siemens S7 client over ISO-on-TCP (see its README) | Plc, Tcp |
| `Junevy.Communication.Omron` | Omron FINS client over FINS/TCP (see its README) | Plc, Tcp |

Install a protocol package; it references this package. Preview packages need `--prerelease`:

```text
dotnet add package Junevy.Communication.Melsec --prerelease
dotnet add package Junevy.Communication.Siemens --prerelease
dotnet add package Junevy.Communication.Omron --prerelease
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
| `bool[]` | `ReadBitsAsync(address, count)` | `WriteBitsAsync(address, values)` |
| `byte` | `ReadByteAsync` | `WriteByteAsync` |
| `byte[]` (bytes, not words) | `ReadBytesAsync(address, count)` | `WriteBytesAsync(address, data)` |
| `byte[]` (normalised words) | `ReadAsync(address, wordCount)` | `WriteAsync(address, data)` |

The write methods have a single-value and an array overload. Multi-word values use the word order of the protocol's data format (`PlcDataFormat`); each protocol picks the default that matches its devices, so MELSEC is `DCBA` with ASCII strings, S7 is `ABCD` and FINS is `CDAB`. A protocol decides its data format, so there is no per-call override.

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

Raw bytes are available through `ReadAsync` and `WriteAsync`. Their layout is the protocol's normalised layout (MELSEC: two bytes per word, little-endian; S7: the CPU's own byte order; FINS: big-endian inside each word), so the same typed conversions apply to every encoding the protocol supports.

## Addresses and Units

An address is a device symbol plus a number, optionally plus a bit index (`D100`, `D100.5`, `M0`, `DB1.DBW12`, `W10.03`). Each device declares the unit its number counts in, and the unit decides how everything else behaves:

| `PlcAddressUnit` | One address is | Bit index | Example protocol |
|---|---|---|---|
| `Bit` | one bit | not allowed | MELSEC `M`, `X`, `Y` |
| `Word` | one 16-bit word | `0`–`15` | MELSEC `D`, FINS `D`, `CIO`, `W` |
| `Byte` | one byte | `0`–`7` | S7 `DB`, `M`, `I`, `Q` |

On a byte device, word access counts two consecutive bytes as one word, so `ReadInt32Async("DB1.DBB0", …)` works on S7 even though the address is a byte address. On a word device, byte access covers the whole words that the requested bytes fall in. A protocol whose areas are all one unit therefore needs no special addressing rules for the typed API.

## Byte Access

`IPlcClient` exposes byte-granular operations next to the word-granular ones:

| Method | Behaviour on a byte device | Behaviour on a word or bit device |
|---|---|---|
| `ReadBytesAsync(address, count)` | reads exactly `count` bytes | reads the `ceil(count / 2)` words that cover them and returns the first `count` bytes of the normalised layout |
| `WriteBytesAsync(address, data)` | writes exactly those bytes | requires a positive even length; otherwise `InvalidRequest` |
| `ReadByteAsync(address)` | reads one byte | reads the covering word and returns the **first byte of the normalised layout**, which is the low byte on MELSEC and the high byte on FINS |
| `WriteByteAsync(address, value)` | writes one byte | `InvalidRequest` (only whole words can be written) |

All four reject addresses that carry a bit index, and a bit access on a byte device addresses the bits inside a byte (bit 0 is the lowest bit).

## Strings

`ReadStringAsync` and `WriteStringAsync` are built on byte access, so their unit rules are the byte rules above:

| Method | Byte device | Word or bit device |
|---|---|---|
| `ReadStringAsync(address, byteLength)` | reads `byteLength` bytes | reads the covering words, uses the first `byteLength` bytes |
| `WriteStringAsync(address, value)` | writes the encoded bytes exactly | pads an odd encoded length with one `0x00` |
| `WriteStringAsync(address, value, byteLength)` | writes the encoded bytes padded with `0x00` to `byteLength` | same, and `byteLength` must be a positive even number |

The fixed-length overload exists because a shorter string written over a longer one otherwise leaves the tail of the old text in the field. `byteLength` must be at least 1, an encoded value that does not fit is `InvalidRequest`, and the encoding is `PlcDataFormat.StringEncoding` (ASCII unless the protocol's options say otherwise). A protocol with its own string type — S7's `String` and `WString`, which carry a length header — adds its own methods in its own package.

## Parsing an Address Without Sending Anything

`IPlcClient.ParseAddress(address)` runs the protocol's parser synchronously and returns `CommResult<PlcAddress>`: `InvalidRequest` with the parser's message when the text is not valid. It performs no I/O, is thread-safe, and still works after the client is disposed, which makes it the check to run when a configuration file or a screen definition is loaded.

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

Retries apply to each request separately: when a large read is split into packets, only the packet that failed is retried. A retry happens only when the link is restored within `RetryInterval`; otherwise the real failure is returned. When the failure dropped the connection (a TCP request timeout with `ResetOnRequestTimeout`, which MELSEC 3E, S7, FINS and Modbus RTU over TCP use, or a connection closed mid-request), the channel has already left `Connected`, so the retry waits for the rebuilt connection rather than resending on the old one.

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

A protocol derives from `PlcClientBase<TAddress>` and implements the hooks that describe its frames. The base class provides the rest: batching into packets, the operation lock, retries, bit routing, timeouts and disposal. See `Junevy.Communication.Melsec`, `Junevy.Communication.Siemens` and `Junevy.Communication.Omron` as implementations, and the `using-junevy-plc` skill in the repository for a step-by-step guide.

Which hooks you have to override is decided by the units your device table contains (P4-D5):

| Hook | Kind | Who implements it |
|---|---|---|
| `TryParseAddress(address, out parsed, out error)` | abstract | every protocol |
| `GetMaxReadPoints(address, access)` / `GetMaxWritePoints(address, access)` | abstract | every protocol. `access` is `Words`, `Bits` or `Bytes`, and the returned count is in that unit |
| `BuildReadWordsRequest` / `ParseReadWordsResponse` / `BuildWriteWordsRequest` / `ParseWriteResponse` | abstract | every protocol; the typed API runs on them |
| `BuildWriteBitsRequest` | abstract | every protocol |
| `BuildReadBitsRequest` / `ParseReadBitsResponse` | virtual, default throws | only protocols with a bit device. A protocol without bit areas never sees a bit read: the suite routes it to byte or word access |
| `BuildReadBytesRequest` / `ParseReadBytesResponse` / `BuildWriteBytesRequest` | virtual, default throws | only protocols with a byte device (S7). `offset` and `count` are in bytes |
| `CanWriteBitsNatively(address)` | virtual, default `address.Unit == PlcAddressUnit.Bit` | protocols that can write a single bit of a word or byte area override it to `true` (FINS, S7). When it is `false`, a word device writes bits by read-modify-write under `AllowReadModifyWriteBits`, and a byte device returns `NotSupported` |
| `ValidateRange(address, access, count)` | virtual, default success | protocols that cannot represent some range override it; it runs before any frame is sent |
| `IsReadOnly(address)` | virtual, default `false` | protocols with read-only areas (for example input registers) override it. A write to a read-only address returns `InvalidRequest` before any point check, without sending a frame or taking the operation lock; reads are not affected |

A default hook that is not overridden throws `InvalidOperationException`, which the pipeline reports as `Unspecified` with an error log — so a protocol never has to write an empty implementation for a hook it does not use.

### Protocol-specific requests (P7-S2)

`SendProtocolRequestAsync<T>(operation, buildRequest, parseResponse, cancellationToken)` (protected) sends one packet for a function code the read and write entry points do not cover (for example Modbus 0x16 and 0x17). It uses the same guards, operation lock, retry rules and error mapping as the entry points: disposed or disconnected returns `NotConnected` without sending, `Timeout`, `ConnectionClosed` and `ProtocolViolation` are retried under `RetryCount`, `RemoteError` and `InvalidRequest` are not, a hook exception becomes `Unspecified` with an error log, and a caller cancellation returns `Cancelled`. A null delegate or a blank `operation` throws synchronously, because it is a programming error.

Protocol state that a handshake discovers (a negotiated PDU size, a node address) belongs in an immutable session snapshot that the `Create` method hands to both the channel's `IConnectionInitializer` and the client, and the hooks read the current snapshot; see the S7 and FINS packages for the pattern.

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

### Wiring the Components

`PlcComponentWiring.Apply` fills in the parts of `ChannelComponents` a protocol owns: how frames are delimited, how a reply is matched to its request, what to do right after the channel opens, and how to probe the link. It returns the same instance and refuses to overwrite a value the caller already set.

```csharp
using System;
using Junevy.Communication.Channels;
using Junevy.Communication.Plc;

public static class ProtocolWiringExample
{
    public static ChannelComponents Configure(ChannelComponents components, MySettings settings, MySession session, ChannelDelivery delivery)
    {
        // Rules that depend on the transport belong to the protocol, and run before the wiring.
        if (delivery != ChannelDelivery.Stream)
            throw new ArgumentException("This protocol runs over a byte stream.", nameof(delivery));

        return PlcComponentWiring.Apply(
            components,
            owner: "MyProtocolProfile",
            frameCodec: MyFraming.Create(),
            correlation: CorrelationMode.Sequential,
            keyExtractor: null,
            initializer: new MyHandshake(settings, session),
            healthProbeFactory: channel => new MyReadProbe(channel));
    }
}
```

The rules:

- The protocol always owns `FrameCodec`, `Correlation`, `KeyExtractor`, `HealthProbe` and `HealthProbeFactory`. If the caller has set any of them, `Apply` throws `ArgumentException` with `ParamName` `"components"` and a message naming the property — it never silently overrides the caller. Pass `frameCodec: null` when the protocol has no framing (a datagram transport), and `keyExtractor: null` when correlation is not `Keyed`.
- `Initializer` belongs to the protocol **only when it supplies one**. With `initializer: null` the property is neither checked nor assigned, so a caller-provided handshake hook survives; with a non-null initializer a preset value is a conflict like the rest.
- `ReconnectPolicy` is left alone — retry backoff is the host's choice, not the protocol's declaration.
- Every guard runs before anything is assigned, so a rejected call leaves the components exactly as they were. The guards are checked in this order: `FrameCodec`, `Correlation`, `KeyExtractor`, `Initializer`, `HealthProbe`, `HealthProbeFactory`.
- Rules that depend on the delivery or the medium (which framings a transport can carry, which frame types match which media) stay in the protocol's own profile, before the call.

### Shared Parts

Two more pieces of the plumbing live in Core so that a new protocol does not write them again:

- `BigEndianFields` writes and takes the fixed-width fields of a header, most significant byte first. Reads return `false` and leave the span untouched when there are not enough bytes, so a decoder reports a structural error through its own result.
- `SequenceCounter` hands out the incrementing values for the field that pairs a reply with its request. Keep one process-wide instance on the type that encodes that header field, so the client's requests and its heartbeat probes take numbers from the same source.

```csharp
using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Utils;

internal static class MyHead
{
    // Process-wide, on the type that writes the field: requests and heartbeat probes share it.
    internal static readonly SequenceCounter Requests = new SequenceCounter();

    public static void WriteRequest(IBufferWriter<byte> buffer, MyRoute route)
    {
        BigEndianFields.WriteUInt32(buffer, 0x00010000);       // length
        BigEndianFields.WriteUInt16(buffer, 0x0102);            // command
        BigEndianFields.WriteUInt8(buffer, route.Unit);
        BigEndianFields.WriteUInt16(buffer, Requests.NextUInt16());
    }
}
```
