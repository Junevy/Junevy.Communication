---
name: using-junevy-plc
description: Use when a .NET project references Junevy.Communication.Plc or Junevy.Communication.Melsec, or needs address-based PLC access (typed reads and writes of device memory) with MELSEC MC 3E or 4E over TCP or UDP, or 4C over serial — writing typed reads and writes, choosing a transport and frame, configuring heartbeat and reconnect, DI or PlcClientFactory registration, interpreting ErrorKind or RemoteError end codes, or adding a new address-based protocol package (deriving PlcClientBase, declaring a device table, implementing protocol hooks and a creator)
---

# Using Junevy PLC (address-based protocol suite)

## Overview

Address-based PLC client suite for .NET (net472 + net8.0) by Junevy, version `1.0.0-preview.1`. MELSEC is the first protocol. Core principles:

- **Result-based**: operations return `CommResult` / `CommResult<T>`. Invalid addresses, device error responses, timeouts and disconnects are results, not exceptions. Branch on `IsSuccess`, then on `ErrorKind`. Never match `ErrorMessage` text.
- **Exceptions only at construction**: invalid configuration throws `ArgumentException` when the client is created. Cancellation returns `CommErrorKind.Cancelled`.
- **The client owns its channel**: `Dispose` / `DisposeAsync` release it. Any later operation returns `NotConnected` with `The PLC client has been disposed.`
- **Explicit typed names**: `WriteInt16Async`, `WriteInt32Async`, `WriteFloatAsync` … There is no `WriteAsync(string, short)` overload family, because a literal `5` would bind to `int` and silently write two words.
- **Data format belongs to the protocol**: `IPlcClient.DataFormat` comes from the protocol options (MELSEC default: word order `DCBA`, ASCII strings). There is no per-call override.
- **Retries are off by default**: `RetryCount = 0`. When a large operation is split into packets, only the packet that failed is retried.

## When NOT to Use

- Modbus TCP or RTU: use `using-junevy-modbus`, which has its own API and result types.
- Byte channels without PLC semantics (raw TCP, UDP or serial): use `using-junevy-channels`.
- S7, FINS, OPC UA and other protocols: not in P2. Only MELSEC exists.
- Real-PLC validation: the MELSEC encoding is checked against the manual's examples only. The two items in "Not Yet Verified" are unverified.

## Package Selection

| Need | Package | Client | Creator | DI extension (namespace) |
|---|---|---|---|---|
| Protocol-neutral suite | `Junevy.Communication.Plc` | `PlcClientBase<TAddress>`, `PlcClientFactory` | `IPlcClientCreator` | `AddPlcClients()` (`Junevy.Communication.Plc.DependencyInjection`) |
| MELSEC MC over TCP, UDP or serial | `Junevy.Communication.Melsec` | `McClient` (`IPlcClient`) | `McClientCreator` | `AddMelsec()` (`Junevy.Communication.Melsec.DependencyInjection`) |

The READMEs in `Junevy.Communication.Plc` and `Junevy.Communication.Melsec` have the complete option defaults, limits and the device table. Read them before you write production code.

## MELSEC Quick Start (TCP)

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

public static class McTcpExample
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = new McClientOptions { FrameType = McFrameType.Frame3E, Encoding = McEncoding.Binary };
        await using McClient plc = McClient.CreateTcp(new TcpClientChannelConfig { Host = "192.168.1.10", Port = 5000 }, options);

        CommResult connect = await plc.ConnectAsync(cancellationToken);
        if (!connect.IsSuccess)
            throw new InvalidOperationException($"Connect failed: {connect.ErrorKind}. {connect.ErrorMessage}");

        CommResult write = await plc.WriteInt16Async("D100", 1234, cancellationToken);
        CommResult<short> read = await plc.ReadInt16Async("D100", cancellationToken);
        if (!write.IsSuccess || !read.IsSuccess)
            throw new InvalidOperationException($"D100 failed: {write.ErrorKind} / {read.ErrorKind}");
    }
}
```

## Choosing a Transport and Frame

| Transport | Frames | Notes |
|---|---|---|
| TCP | `Frame3E`, `Frame4E` | Length-field framing set by the profile. 4E replies are matched by serial number |
| UDP | `Frame3E`, `Frame4E` | Datagrams. A late 4E reply is discarded, so the next request gets its own reply |
| Serial | `Frame4C` only | `McEncoding.Ascii` selects format 4 (ASCII, ENQ / ACK / NAK / STX ETX); `McEncoding.Binary` selects format 5 (DLE framing). `SumCheck` must match the PLC |

- `McClient.CreateTcp` / `CreateUdp` / `CreateSerial` reject a frame type that the transport does not support with `ArgumentException`.
- The transport configuration must keep its default framing and correlation. The profile sets them; a changed value throws `ArgumentException` instead of being overridden silently.
- Creating a client never opens a socket or a serial port. `ConnectAsync` does.

## Typed API

| Type | Read | Write |
|---|---|---|
| `short` / `ushort` | `ReadInt16Async` / `ReadUInt16Async` | `WriteInt16Async` / `WriteUInt16Async` |
| `int` / `uint` | `ReadInt32Async` / `ReadUInt32Async` | `WriteInt32Async` / `WriteUInt32Async` |
| `long` / `ulong` | `ReadInt64Async` / `ReadUInt64Async` | `WriteInt64Async` / `WriteUInt64Async` |
| `float` / `double` | `ReadFloatAsync` / `ReadDoubleAsync` | `WriteFloatAsync` / `WriteDoubleAsync` |
| `string` | `ReadStringAsync(address, byteLength)` (truncated at the first NUL) | `WriteStringAsync(address, value)` |
| `bool` | `ReadBoolAsync` | `WriteBoolAsync` |

- Each read has a single-value form and a `count` form that returns an array. Each write has a single-value and an array overload.
- Raw bytes: `ReadAsync` / `WriteAsync` use the protocol's normalised layout (MELSEC: two bytes per word, little-endian, words in address order), so the same conversions apply to every encoding.

## Addresses

- Format `<device><number>[.<bit>]`, case-insensitive: `D100`, `M0`, `X1A`, `ZR0`, `D100.F`.
- The number uses the device's radix. `X`, `Y`, `B`, `W`, `SB`, `SW`, `DX`, `DY` and `ZR` are hexadecimal.
- The bit index is hexadecimal `0` to `F`: `D100.F` is bit 15 of `D100`.
- Reading a bit of a word device works (the word is read and the bit selected). Writing one returns `NotSupported` unless `AllowReadModifyWriteBits = true`; the library then does a read-modify-write under the operation lock and logs a warning on every write. Use it only when no PLC program writes the same word.
- Numbers above the frame's limit return `InvalidRequest`: `0xFFFFFF` for binary frames and hexadecimal devices in ASCII frames, `999999` for decimal devices in ASCII frames.

## Limits and Splitting

| Request | Points per request |
|---|---|
| Word read or write | 960 |
| Bit read or write, Ethernet binary | 7168 |
| Bit read or write, Ethernet ASCII | 3584 |
| Bit read or write, serial | 7904 |

Larger operations are split automatically (2000 words become 960, 960, 80). The library does not return partial data: if one packet fails, the whole operation returns that failure.

## Options and Retries

`McClientOptions` derives from `PlcClientOptions`. Important defaults: `FrameType` `Frame3E`, `Encoding` `Binary`, `SumCheck` `true`, `MonitoringTimer` `0x0004` (250 ms units, so 1 s), `RetryCount` `0`, `RetryInterval` `1000` ms, `AllowReadModifyWriteBits` `false`. The monitoring timer is kept below the transport's `RequestTimeout` (2000 ms), so the PLC answers with an end code before the client times out (P2-D15). If you raise the monitoring timer, raise `RequestTimeout` too.

## Error Handling

```csharp
CommResult<short> result = await plc.ReadInt16Async("D100", cancellationToken);
if (!result.IsSuccess)
{
    switch (result.ErrorKind)
    {
        case CommErrorKind.RemoteError:
            // ProtocolErrorCode is the raw MC end code, for example 0xC051. Look it up in the manual.
            break;
        case CommErrorKind.NotConnected:
            // Wait for the link with WaitForConnectedAsync, or enable Reconnect.
            break;
    }
}
```

| `ErrorKind` | Meaning | Action |
|---|---|---|
| `InvalidRequest` | Address cannot be parsed, a point count is out of range, or a number exceeds the frame limit | Fix the request. Not retried |
| `NotConnected` | The channel is not connected when the operation starts, or the client is disposed | Wait or reconnect. Not retried by the library |
| `RemoteError` | The PLC returned an end code. `ProtocolErrorCode` has the raw code; the message is `MC end code 0xXXXX.` | Look up the code in the manual. Not retried |
| `Timeout`, `ConnectionClosed`, `ProtocolViolation` | Transport or protocol failure. A reply that does not match the request (for example a loopback echo mismatch) is a violation | Retried when `RetryCount > 0` and the link returns within `RetryInterval`; otherwise the real failure is returned |
| `NotSupported` | The operation is not allowed, for example a bit write into a word device | Enable the option only if you accept the risk |
| `Cancelled` | The caller's token was signalled | Expected on shutdown |

## Heartbeat and Reconnect

- The heartbeat sends the loopback test (0619) with the fixed data `1234ABCD` and expects the same bytes back. It is off by default: set `Heartbeat.Enabled = true` in the transport configuration.
- Reconnect is also off by default: set `Reconnect.Enabled = true`. The channel then reconnects in the background.
- Subscribe to `StateChanged` to see `Reconnecting` with reasons such as `HeartbeatFailed` or `RemoteClosed`.

## Dependency Injection

```csharp
using System;
using Junevy.Communication.Melsec.DependencyInjection;
using Junevy.Communication.Plc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class McDependencyInjectionExample
{
    public static void Register(IServiceCollection services) => services.AddMelsec();

    public static IPlcClient Create(IServiceProvider provider, IConfiguration configuration)
    {
        McClientConfig config = configuration.GetSection("Mc").Get<McClientConfig>()
            ?? throw new InvalidOperationException("The Mc section is missing.");
        return provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line-1", config);
    }
}
```

- `AddMelsec()` registers `McClientCreator` and calls `AddPlcClients()`. It is idempotent.
- `PlcClientFactory` selects the creator by the exact runtime type of the configuration. No creator for the type throws `NotSupportedException`.
- `GetOrAdd` with an existing name returns the existing client and ignores the configuration.
- `McClientConfig.Transport` is `Tcp`, `Udp` or `Serial`. The matching section (`Tcp`, `Udp`, `Serial`) is required. A serial client needs `"FrameType": "Frame4C"`.

## Troubleshooting

- **Connect succeeds, every read times out**: check the frame type against the PLC's communication settings (3E and 4E are different), the access route (`NetworkNumber`, `PcNumber`, `ModuleIoNumber`, `ModuleStationNumber`), and the monitoring timer against `RequestTimeout`.
- **Every reply is a `ProtocolViolation`**: the encoding (`Binary` or `Ascii`) or the frame type does not match the PLC.
- **`RemoteError` with a code you do not know**: look it up in the manual for the command in use. The library does not translate end codes (P2-D12).
- **Serial replies fail the sum check**: `SumCheck` must match the PLC. Format 4 and format 5 use different sum ranges.
- **`InvalidRequest` for a large decimal number in ASCII**: decimal numbers in ASCII frames are at most 999999.
- **`NotSupported` on a bit write**: the target is a bit of a word device. Write the word, or enable `AllowReadModifyWriteBits` knowingly.

## Not Yet Verified (P2-D16)

Both items are inferred from the manual's figures and tested only against the manual's examples. Verify on hardware before you rely on them:

1. The format 4 response sum check covers the bytes from the frame identifier through ETX.
2. The format 5 error response's 2-byte end code is read as little-endian.

## Protocol Author Guide

Use this section when you add a protocol package (for example `Junevy.Communication.<Name>`). MELSEC is the reference implementation.

### Package layout

```
Junevy.Communication.<Name>/
  <Name>AddressParser or device table   declares the devices (a PlcAddressParser<TDevice, TAddress>)
  <Name>Address.cs                      derives PlcAddress (device, number, bit, text)
  <Name>ClientOptions.cs                derives PlcClientOptions
  Protocol/                             frame encoding and decoding, with no channel dependency
  <Name>Client.cs                       derives PlcClientBase<TAddress>; hooks and static factories
  DependencyInjection/                  config type (IPlcClientConfig), creator (IPlcClientCreator), AddX()
Junevy.Communication.<Name>.Tests/
  TestSupport/                          device adapter (PlcMemory + DeviceSimulator), golden vectors
```

### Device table and address type

- A device implements `IPlcDeviceDefinition`: `Symbol`, `Unit` (`PlcAddressUnit.Bit` or `Word`), `Radix` (10 or 16) and `MaxNumber`.
- Declare the table as an array and build the parser once: `new PlcAddressParser<TDevice, TAddress>(devices, (device, number, bit, text) => new TAddress(...), bitIndexRadix: 16)`. The parser is case-insensitive and picks the longest symbol (`ZR` before `Z`). `TryParse(text, out address, out error)` puts a readable message in `error` on failure, and the client reports it as `InvalidRequest`.
- `PlcAddress.Device` is typed as `IPlcDeviceDefinition`. A derived address that needs the concrete device declares `public new McDevice Device => (McDevice)base.Device;`.

### Hooks

`PlcClientBase<TAddress>` declares its hooks `protected internal`. A protocol in another assembly overrides them with `protected override`.

| Hook | Shape |
|---|---|
| `TryParseAddress` | `bool TryParseAddress(string address, out TAddress? parsed, out string? error)` |
| `GetMaxReadPoints` / `GetMaxWritePoints` | `int (TAddress address, PlcAccess access)` |
| `BuildReadWordsRequest` | `void (TAddress address, int offset, int count, IBufferWriter<byte> output)` |
| `ParseReadWordsResponse` | `CommResult<byte[]> (int count, ReadOnlySpan<byte> frame)` (normalised bytes) |
| `BuildWriteWordsRequest` | `void (TAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output)` |
| `BuildReadBitsRequest` | `void (TAddress address, int offset, int count, IBufferWriter<byte> output)` |
| `ParseReadBitsResponse` | `CommResult<bool[]> (int count, ReadOnlySpan<byte> frame)` |
| `BuildWriteBitsRequest` | `void (TAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output)` |
| `ParseWriteResponse` | `CommResult (ReadOnlySpan<byte> frame)` |
| `ValidateRange` (virtual) | `CommResult (TAddress address, PlcAccess access, int count)`; returns `InvalidRequest` for requests the protocol cannot represent |

The `Build*` hooks return `void`, so they cannot report an error. Put representability checks (device number ranges, point limits per encoding) in `ValidateRange`. The base class calls it before any frame is built.

### Transport profile and factories

- Declare the channel components for each transport in a profile: a `FrameCodec` (for example a length field or a DLE codec), `Correlation`, an optional `KeyExtractor` and a `HealthProbeFactory` for the heartbeat.
- Guard the profile: if the caller already set one of these components, throw `ArgumentException`. Do not override it silently.
- Provide two static factories per transport. The public one takes `(TransportConfig, options, ILoggerFactory?)`. The internal one takes `(string? name, TransportConfig, options, ILoggerFactory?)` for the DI creator, so the channel gets the client's name in its logs.
- Check the transport configuration's defaults (framing, correlation) and throw `ArgumentException` when they differ.

### Creator, DI and tests

- The creator's `ConfigType` is the exact config type. `Create` validates the configuration, throws `ArgumentException` when it is wrong, and never modifies the caller's object.
- `AddX()` calls `services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlcClientCreator, XClientCreator>())` and then `AddPlcClients()`.
- Test the frame encoding against the manual's golden vectors, in both directions where the manual gives both. Test the device adapter with `PlcMemory` and `DeviceSimulator`, and run the end-to-end matrix over every transport.
- Wait for conditions by polling. Do not use `Thread.Sleep`.
- `DuplexClientChannel`'s `onOpened` callback is awaited before the connection completes. Start the simulator inside it without returning its task, or the test hangs.

### What you must write and what you do not

| Must write | Not needed (the suite provides it) |
|---|---|
| Device table and address type | Connection, reconnect, heartbeat scheduling, timeouts |
| The ten protocol hooks and `ValidateRange` if the limits depend on the frame | Stream framing as such (you declare it in the profile) |
| Frame codecs, with golden-vector tests | Request serialisation, retries, batching |
| Transport profile and client factories | Typed reads and writes, byte-order conversion |
| Creator and the `AddX()` extension | Logging, the registry, the factory |
| Device adapter and end-to-end tests | Transport adapters (TCP, UDP, serial) |

## Lessons From the P2 Friction Log

These are the traps the MELSEC implementation hit. The design document's section 20.2 has the full entries.

- `ConnectionStatistics` and `ConnectionStateChangedEventArgs` have internal constructors. A test double that implements `IClientChannel` needs reflection or a Channels test kit. Prefer `DuplexClientChannel` from `Junevy.Communication.Testing`.
- `PlcAddress.Device` is the interface type, so a derived address exposes a shadowing `new` property. Callers that need the concrete device must cast.
- The `Build*` hooks cannot report errors. An ASCII decimal number above 999999 cannot be encoded, so the check belongs in `ValidateRange`, and the encoder keeps a matching guard.
- `IFrameCodecFactory` does not receive `FramingOptions.MaxFrameLength`. A custom codec factory must take the limit separately and keep it consistent with the channel's configuration.
- `ChannelComponents` silently overrides a same-named transport configuration setting. Guard the profile (see above).
- `PassthroughFrameEncoder` is internal to Channels. Borrow a Raw-mode codec factory from `FrameCodecFactory.Create(new FramingOptions())` when you need pass-through encoding.
- A format 5 decoder must skip escaped `10H 10H` pairs before it finds an unescaped DLE ETX. Channels has no forward byte reader, so the current decoder copies its window and rescans it on each arrival.
- `SequenceFactory` (Testing) builds segmented `ReadOnlySequence<byte>` values. Use it for decoder tests that span buffer boundaries.
- `DuplexClientChannel`'s `onOpened` is awaited. Returning a long-running task from it hangs the connect (see above).
