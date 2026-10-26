---
name: using-junevy-plc
description: Use when a .NET project references Junevy.Communication.Plc, Junevy.Communication.Melsec, Junevy.Communication.Siemens or Junevy.Communication.Omron, or needs address-based PLC access (typed reads and writes of device memory) with MELSEC MC 3E/4E over TCP or UDP and 4C over serial, Siemens S7comm over ISO-on-TCP, or Omron FINS over TCP — writing typed reads and writes, byte- and word-granular access, strings, choosing a transport, configuring heartbeat and reconnect, DI or PlcClientFactory registration, interpreting ErrorKind or RemoteError end codes, or adding a new address-based protocol package (deriving PlcClientBase, declaring a device table, implementing protocol hooks and a creator)
---

# Using Junevy PLC (address-based protocol suite)

## Overview

Address-based PLC client suite for .NET (net472 + net8.0) by Junevy, version `1.0.0-preview.1`. Four protocols sit on it: MELSEC MC, Siemens S7, Omron FINS and Modbus (see the Modbus Quick Start). Core principles:

- **Result-based**: operations return `CommResult` / `CommResult<T>`. Invalid addresses, device error responses, timeouts and disconnects are results, not exceptions. Branch on `IsSuccess`, then on `ErrorKind`. Never match `ErrorMessage` text.
- **Exceptions only at construction**: invalid configuration throws `ArgumentException` when the client is created. Cancellation returns `CommErrorKind.Cancelled`.
- **The client owns its channel**: `Dispose` / `DisposeAsync` release it. Any later operation returns `NotConnected` with `The PLC client has been disposed.`
- **Explicit typed names**: `WriteInt16Async`, `WriteInt32Async`, `WriteFloatAsync` … There is no `WriteAsync(string, short)` overload family, because a literal `5` would bind to `int` and silently write two words.
- **Data format belongs to the protocol**: `IPlcClient.DataFormat` comes from the protocol options (MELSEC default: word order `DCBA`, ASCII strings; S7: `ABCD`; FINS: `CDAB`). There is no per-call override.
- **Retries are off by default**: `RetryCount = 0`. When a large operation is split into packets, only the packet that failed is retried.

## When NOT to Use

- Modbus TCP, RTU or UDP is a protocol on this suite (`ModbusClient`, see the Modbus Quick Start below). `using-junevy-modbus` is the v2 to v3 migration guide.
- Byte channels without PLC semantics (raw TCP, UDP or serial): use `using-junevy-channels`.
- FINS/UDP, Host Link over serial, S7comm-plus or symbolic access, OPC UA, SECS/GEM: not implemented. S7 and FINS here are TCP-only, and S7 is only claimed for S7-1200/1500.
- Real-PLC validation: none of the three protocols has been checked against real hardware. MELSEC is verified against the manual's examples, S7 and FINS against the P4 plan's golden vectors. See "Not Yet Verified".

## Package Selection

| Need | Package | Client | Creator | DI extension (namespace) |
|---|---|---|---|---|
| Protocol-neutral suite | `Junevy.Communication.Plc` | `PlcClientBase<TAddress>`, `PlcClientFactory` | `IPlcClientCreator` | `AddPlcClients()` (`Junevy.Communication.Plc.DependencyInjection`) |
| MELSEC MC over TCP, UDP or serial | `Junevy.Communication.Melsec` | `McClient` (`IPlcClient`) | `McClientCreator` | `AddMelsec()` (`Junevy.Communication.Melsec.DependencyInjection`) |
| Siemens S7-1200/1500 over ISO-on-TCP (port 102) | `Junevy.Communication.Siemens` | `S7Client` (`IPlcClient`) | `S7ClientCreator` | `AddSiemensS7()` (`Junevy.Communication.Siemens.DependencyInjection`) |
| Omron FINS over TCP (port 9600) | `Junevy.Communication.Omron` | `FinsClient` (`IPlcClient`) | `FinsClientCreator` | `AddOmronFins()` (`Junevy.Communication.Omron.DependencyInjection`) |
| Modbus TCP, RTU or UDP (MBAP and RTU frames) | `Junevy.Communication.Modbus` | `ModbusClient` (`IPlcClient`) | `ModbusClientCreator` | `AddModbus()` (`Junevy.Communication.Modbus.DependencyInjection`) |

The READMEs in `Junevy.Communication.Plc`, `Junevy.Communication.Melsec`, `Junevy.Communication.Siemens` and `Junevy.Communication.Omron` have the complete option defaults, limits and device tables. Read them before you write production code.

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
        await using McClient plc = McClient.Create(new TcpClientChannelConfig { Host = "192.168.1.10", Port = 5000 }, options);

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

## S7 Quick Start (ISO-on-TCP, port 102)

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens;
using Junevy.Communication.Tcp;

public static class S7SkillExample
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = new S7ClientOptions { Rack = 0, Slot = 1 };
        await using S7Client plc = S7Client.Create(new TcpClientChannelConfig { Host = "192.168.1.20", Port = 102 }, options);

        CommResult connect = await plc.ConnectAsync(cancellationToken);
        if (!connect.IsSuccess)
            throw new InvalidOperationException($"Connect failed: {connect.ErrorKind}. {connect.ErrorMessage}");

        CommResult<short> level = await plc.ReadInt16Async("DB1.DBW12", cancellationToken);
        CommResult<byte[]> flags = await plc.ReadBytesAsync("MB0", 16, cancellationToken);
        CommResult<string> name = await plc.ReadS7StringAsync("DB1.DBB100", 20, cancellationToken);
        if (!level.IsSuccess || !flags.IsSuccess || !name.IsSuccess)
            throw new InvalidOperationException("The S7 read failed.");
    }
}
```

- The connect runs a two-step handshake (COTP CR/CC, then Setup Communication). `plc.NegotiatedPduSize` reports what the CPU agreed to and decides the packet limits.
- On the CPU: permit PUT/GET access, and uncheck *Optimized block access* on every data block you address. Without those two settings the CPU answers `0x8104` or an item return code of `0x03`.
- Only `TcpClientChannelConfig` is accepted; any other transport type throws `NotSupportedException` (S7 has no UDP, serial or S7comm-plus route in this package).

## FINS Quick Start (TCP, port 9600)

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

public static class FinsSkillExample
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = new FinsClientOptions { ClientNode = 0 };
        await using FinsClient plc = FinsClient.Create(new TcpClientChannelConfig { Host = "192.168.1.30", Port = 9600 }, options);

        CommResult connect = await plc.ConnectAsync(cancellationToken);
        if (!connect.IsSuccess)
            throw new InvalidOperationException($"Connect failed: {connect.ErrorKind}. {connect.ErrorMessage}");

        CommResult<short> level = await plc.ReadInt16Async("D100", cancellationToken);
        CommResult<bool> running = await plc.ReadBoolAsync("CIO0.0", cancellationToken);
        byte? serverNode = plc.ServerNode;
        if (!level.IsSuccess || !running.IsSuccess)
            throw new InvalidOperationException("The FINS read failed.");
    }
}
```

- The connect sends a FINS/TCP node-address request; `ClientNode = 0` lets the CPU assign the node number. `plc.ClientNode` and `plc.ServerNode` are `null` until that handshake has run, and a reconnect re-runs it, so the numbers can change.
- `ClientNode = 255` throws at `Create`: it is the FINS broadcast address.
- Only `TcpClientChannelConfig` is accepted; FINS/UDP and Host Link are not implemented.

## Modbus Quick Start (TCP, RTU and UDP)

Modbus is the fourth protocol on the suite (package `Junevy.Communication.Modbus`, version `3.0.0-preview.1`). The client is `ModbusClient : PlcClientBase<ModbusAddress>`, so the whole typed API applies. Create it with `ModbusClient.Create`; there is no public constructor.

```csharp
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

using ModbusClient client = ModbusClient.Create(
    new TcpClientChannelConfig { Host = "192.168.1.10", Port = 502 },
    new ModbusClientOptions { UnitId = 1 });

await client.ConnectAsync();
CommResult<ushort[]> registers = await client.ReadUInt16Async("HR100", 2);
CommResult<bool[]> coils = await client.ReadBitsAsync("C0", 8);
```

Address forms on this client: symbolic zero-based (`HR100`, `C5`, `DI7`, `IR3`), Modicon one-based (`40101`, `400101`), a register bit (`HR100.5`) and a station prefix (`s=2;HR100`). A coil or discrete input has no bit index, and an input register or discrete input cannot be written.

On serial the framing defaults to RTU. `ModbusClientOptions.Framing = ModbusFraming.Rtu` on a TCP configuration gives RTU over TCP (gateways and serial servers). `ModbusFraming.Mbap` on serial is rejected at creation.

Use `ModbusClientOptions.UseSingleWriteFunctions = false` for devices that accept only 0F and 10. Use `UseMaskWriteForBits = true` to write a register bit with 0x16; a device without 0x16 answers exception 01 (`RemoteError`, `ProtocolErrorCode` 1), and then the fallback is `UseMaskWriteForBits = false` with `AllowReadModifyWriteBits = true` (03 then 06). The Modbus README has the function-code map, the packet limits (125 and 2000 read, 123 and 1968 write), the error kinds and the migration table from 2.x.

Modbus-only operations: `MaskWriteRegisterAsync(address, andMask, orMask)` (0x16) and `ReadWriteMultipleRegistersAsync(readAddress, readCount, writeAddress, writeData)` (0x17). Neither splits into packets.

## Choosing a Transport and Frame

| Transport | Frames | Notes |
|---|---|---|
| MELSEC TCP | `Frame3E`, `Frame4E` | Length-field framing set by the profile. 4E replies are matched by serial number |
| MELSEC UDP | `Frame3E`, `Frame4E` | Datagrams. A late 4E reply is discarded, so the next request gets its own reply |
| MELSEC serial | `Frame4C` only | `McEncoding.Ascii` selects format 4 (ASCII, ENQ / ACK / NAK / STX ETX); `McEncoding.Binary` selects format 5 (DLE framing). `SumCheck` must match the PLC |
| S7 ISO-on-TCP | port 102 | TPKT length framing, replies matched in send order. Stream delivery only: a datagram creator throws `ArgumentException` |
| FINS TCP | port 9600 | FINS/TCP shell length framing, replies matched in send order. Stream delivery only |

- `McClient.Create` rejects a frame type that the transport does not support with `ArgumentException`. A transport configuration of a type that has no built-in channel creator throws `NotSupportedException`.
- A transport that is not built in is created with its own `IChannelCreator`: `McClient.Create(transport, creator, options)` (same shape on `S7Client` and `FinsClient`). The creator's `Delivery` and `Medium` decide which frame types the protocol accepts for it.
- The transport configuration must keep its default framing and correlation. The profile sets them; a changed value throws `ArgumentException` instead of being overridden silently.
- Creating a client never opens a socket or a serial port. `ConnectAsync` does, and for S7 and FINS it also runs the protocol handshake.

## Typed API

| Type | Read | Write |
|---|---|---|
| `short` / `ushort` | `ReadInt16Async` / `ReadUInt16Async` | `WriteInt16Async` / `WriteUInt16Async` |
| `int` / `uint` | `ReadInt32Async` / `ReadUInt32Async` | `WriteInt32Async` / `WriteUInt32Async` |
| `long` / `ulong` | `ReadInt64Async` / `ReadUInt64Async` | `WriteInt64Async` / `WriteUInt64Async` |
| `float` / `double` | `ReadFloatAsync` / `ReadDoubleAsync` | `WriteFloatAsync` / `WriteDoubleAsync` |
| `string` | `ReadStringAsync(address, byteLength)` (truncated at the first NUL) | `WriteStringAsync(address, value)` |
| `bool` | `ReadBoolAsync` | `WriteBoolAsync` |
| `bool[]` | `ReadBitsAsync(address, count)` | `WriteBitsAsync(address, values)` |
| `byte` | `ReadByteAsync` | `WriteByteAsync` |
| `byte[]` (bytes) | `ReadBytesAsync(address, count)` | `WriteBytesAsync(address, data)` |

- Each read has a single-value form and a `count` form that returns an array. Each write has a single-value and an array overload.
- Raw words: `ReadAsync(address, wordCount)` / `WriteAsync` use the protocol's normalised layout (MELSEC: little-endian per word, words in address order; S7: the CPU's byte order; FINS: big-endian per word), so the same conversions apply to every encoding.
- Byte access (`ReadBytesAsync`, `WriteBytesAsync`, `ReadByteAsync`, `WriteByteAsync`) is exact on a byte device (S7). On a word or bit device (MELSEC, FINS) a read takes the covering words and returns the first `count` normalised bytes, and a write must be a positive even length. `ReadByteAsync` on a word device returns the **first byte of the normalised layout**: the low byte on MELSEC, the high byte on FINS.
- String rules: `WriteStringAsync(address, value)` pads an odd encoded length with `0x00` on word and bit devices and writes byte-exactly on byte devices. `WriteStringAsync(address, value, byteLength)` writes a fixed, zero-padded field (a word or bit device needs an even `byteLength`); use it when a shorter string would otherwise leave the tail of the previous one behind. S7's `String` and `WString` types, which carry a length header, are read and written with `ReadS7StringAsync` / `WriteS7StringAsync` / `ReadS7WStringAsync` / `WriteS7WStringAsync`.
- `ParseAddress(address)` runs the protocol's parser synchronously and returns `CommResult<PlcAddress>` (`InvalidRequest` plus the parser's message when the text is wrong). No I/O, thread-safe, works after dispose — use it to validate addresses when a configuration or screen definition is loaded.

## Addresses

Each protocol has its own syntax; a number is decimal or hexadecimal per device, and the unit decides what one address counts.

| Protocol | Examples | Unit of one address | Bit index |
|---|---|---|---|
| MELSEC | `D100`, `M0`, `X1A`, `ZR0`, `D100.F` | bit device = one bit, word device = one word | hexadecimal `0`–`F`; `D100.F` is bit 15 of `D100` |
| S7 | `DB1.DBW12`, `DB1.DBX12.3`, `MB12`, `MW12`, `MD12`, `%I0.1` | byte (all four areas) | decimal `0`–`7`; `X` requires one, `B`, `W`, `D` forbid one |
| FINS | `D100`, `CIO0.0`, `W10.03`, `E3_200`, `H0` | word | decimal `0`–`15`; `W10.03` is bit 3 of `W10` |

- MELSEC numbers use the device's radix. `X`, `Y`, `B`, `W`, `SB`, `SW`, `DX`, `DY` and `ZR` are hexadecimal.
- S7 width letters (`B`, `W`, `D`) are spelling only: they do not limit how many bytes or words a call reads; the method decides. Numbers are decimal, and the parser rejects an impossible range before anything is sent (data block `1`–`65535`, byte `0`–`2097151`).
- FINS `EM` banks are thirteen symbols `E0_`–`EC_` (the group digit is hexadecimal, the number after it is decimal). Bit writes into a word area are native and continue into the next word: `WriteBitsAsync("W10.15", …)` puts the second value into `W11.0`.
- Reading a bit of a word device always works (the word or byte is read and the bit selected). Writing one is native when the protocol overrides `CanWriteBitsNatively` (S7, FINS); on MELSEC it returns `NotSupported` unless `AllowReadModifyWriteBits = true`, in which case the library reads and rewrites the word under the operation lock and logs a warning on every write. Use that only when no PLC program writes the same word. Writing bits of a byte device without native support is always `NotSupported`.
- Numbers above the frame's or the address field's limit return `InvalidRequest`: MELSEC `0xFFFFFF` for binary frames and hexadecimal devices in ASCII frames, `999999` for decimal devices in ASCII frames; S7 and FINS when the *last* byte or word of the operation passes the address field.

## Limits and Splitting

MELSEC (points per request): word read or write 960; bit read or write 7168 on Ethernet binary, 3584 on Ethernet ASCII, 7904 on serial.

S7 limits come from the PDU size the CPU negotiated at connect (`PDU − 18` bytes read, `PDU − 28` bytes written, half as many words, and one bit per request). With the usual answers:

| Negotiated PDU | Read bytes | Write bytes | Read words | Write words |
|---|---|---|---|---|
| 240 (typical S7-1200) | 222 | 212 | 111 | 106 |
| 960 (typical S7-1500) | 942 | 932 | 471 | 466 |

FINS is fixed: 999 points per read, 996 per write, whether the operation counts words, bits or the words that cover a byte range. It does not depend on the CPU model.

Larger operations are split automatically (2000 words become 960, 960, 80 on MELSEC). The library does not return partial data: if one packet fails, the whole operation returns that failure.

## Options and Retries

Every options class derives from `PlcClientOptions` (`RetryCount` `0`, `RetryInterval` `1000` ms, `AllowReadModifyWriteBits` `false`). The client validates the options and takes a snapshot at construction, so changing the object afterwards has no effect.

- `McClientOptions`: `FrameType` `Frame3E`, `Encoding` `Binary`, `SumCheck` `true`, `MonitoringTimer` `0x0004` (250 ms units, so 1 s), access-route numbers. The monitoring timer is kept below the transport's `RequestTimeout` (2000 ms), so the PLC answers with an end code before the client times out (P2-D15). If you raise it, raise `RequestTimeout` too.
- `S7ClientOptions`: `Rack` `0` (0–7), `Slot` `1` (0–31), `ConnectionType` `PG` (`PG` / `OP` / `Basic`), `DataFormat` (`null` = `ABCD` + ASCII). Some CPUs only accept one connection type, which shows up as a handshake error.
- `FinsClientOptions`: `ClientNode` `0` (assigned by the PLC; `1`–`254` fixed; `255` throws), `DestinationNetwork` `0` (`DNA`), `DestinationUnit` `0` (`DA2`), `DataFormat` (`null` = `CDAB` + ASCII).

## Error Handling

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;

public static class McErrorHandlingExample
{
    public static async Task<short?> ReadSpeedAsync(IPlcClient plc, CancellationToken cancellationToken = default)
    {
        CommResult<short> result = await plc.ReadInt16Async("D100", cancellationToken);
        if (result.IsSuccess)
            return result.Data;

        switch (result.ErrorKind)
        {
            case CommErrorKind.RemoteError:
                // ProtocolErrorCode is the raw MC end code, for example 0xC051. Look it up in the manual.
                break;
            case CommErrorKind.NotConnected:
                // Wait for the link with WaitForConnectedAsync, or enable Reconnect.
                break;
        }

        return null;
    }
}
```

| `ErrorKind` | Meaning | Action |
|---|---|---|
| `InvalidRequest` | Address cannot be parsed, a point count is out of range, or a number exceeds the frame limit | Fix the request. Not retried |
| `NotConnected` | The channel is not connected when the operation starts, or the client is disposed | Wait or reconnect. Not retried by the library |
| `RemoteError` | The PLC returned an error code. `ProtocolErrorCode` has the raw code; the message is `MC end code 0xXXXX.`, `S7 item return code 0xXX (…).` / `S7 error class 0xXX code 0xXX.`, or `FINS end code 0xXXXX.` / `FINS/TCP error 0xXXXXXXXX.` | Look up the code in the manual. Not retried |
| `Timeout`, `ConnectionClosed`, `ProtocolViolation` | Transport or protocol failure. A reply that does not match the request (for example a loopback echo mismatch) is a violation | Retried when `RetryCount > 0` and the link returns within `RetryInterval`; otherwise the real failure is returned. If the failure dropped the connection (TCP request timeout on a sequential protocol, closed connection), the retry waits for the rebuilt connection |
| `NotSupported` | The operation is not allowed, for example a bit write into a word device | Enable the option only if you accept the risk |
| `Cancelled` | The caller's token was signalled | Expected on shutdown |

Protocol-specific details:

- **S7**: the header error class and code are combined as `class << 8 | code`, so `0x8104` is "application relationship / object not accessible". Item return codes other than `0xFF` arrive as `RemoteError` with the code itself. `0x8104` and the item codes `0x03`, `0x05`, `0x0A` carry an extra sentence in the message about PUT/GET and optimized block access.
- **FINS**: the end code's status flags are masked before the result is judged (major `& 0x7F`, minor `& 0x3F`); a masked zero is a **success** even when the raw code was `0x0040`, and the client logs the raw value at debug level. `ProtocolErrorCode` is the masked code. A non-zero error field in the FINS/TCP shell is a separate `RemoteError` (`FINS/TCP error 0x…`).
- Neither package translates error codes into text beyond the code table the frames define (P2-D12). Match `ProtocolErrorCode`, never the message.

## Heartbeat and Reconnect

- MELSEC's heartbeat sends the loopback test (0619) with the fixed data `1234ABCD` and expects the same bytes back.
- S7's heartbeat reads one byte at `MB0`; FINS reads one word at `D0`. Both judge health by the *structure* of the answer, not its content: a CPU that replies with a valid reply is alive even if the reply carries an error code, because that already proves the link and the protocol stack work (P4-D15).
- All three protocols re-run their handshake on every (re)connect: S7 does COTP + Setup Communication again and re-derives the packet limits; FINS asks for node addresses again and adopts whatever numbers the CPU returns. A snapshot of the result is stored immutably and read by the hooks, so a limit or route is never half-updated.
- Heartbeat and reconnect are off by default: set `Heartbeat.Enabled = true` and `Reconnect.Enabled = true` in the transport configuration.
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

- `AddMelsec()` registers `McClientCreator` and calls `AddPlcClients()`. It is idempotent. `AddSiemensS7()` and `AddOmronFins()` do the same for their protocols.
- `PlcClientFactory` selects the creator by the exact runtime type of the configuration. No creator for the type throws `NotSupportedException`.
- `GetOrAdd` with an existing name returns the existing client and ignores the configuration.
- `McClientConfig.Transport` is `Tcp`, `Udp` or `Serial`. The matching section (`Tcp`, `Udp`, `Serial`) is required. A serial client needs `"FrameType": "Frame4C"`.
- `S7ClientConfig` and `FinsClientConfig` have no `Transport` switch (both protocols are TCP-only): they carry `Tcp` and `Options`. A missing `Tcp` object makes the creator throw `ArgumentException`. `PlcDataFormat` has no parameterless constructor, so the data format cannot come from JSON — set it in code.

## Troubleshooting

- **Connect succeeds, every read times out**: check the frame type against the PLC's communication settings (3E and 4E are different), the access route (`NetworkNumber`, `PcNumber`, `ModuleIoNumber`, `ModuleStationNumber`), and the monitoring timer against `RequestTimeout`.
- **Every reply is a `ProtocolViolation`**: the encoding (`Binary` or `Ascii`) or the frame type does not match the PLC.
- **`RemoteError` with a code you do not know**: look it up in the manual for the command in use. The library does not translate end codes (P2-D12).
- **Serial replies fail the sum check**: `SumCheck` must match the PLC. Format 4 and format 5 use different sum ranges.
- **`InvalidRequest` for a large decimal number in ASCII**: decimal numbers in ASCII frames are at most 999999.
- **`NotSupported` on a bit write**: the target is a bit of a word device that cannot write bits natively (MELSEC). Write the word, or enable `AllowReadModifyWriteBits` knowingly. S7 and FINS write bits natively and never return this.
- **S7: the connect succeeds but every read is `0x8104` or item code `0x03`**: PUT/GET is not permitted on the CPU, or the data block still uses optimized block access. Both are CPU-side settings, not client settings.
- **S7: the handshake fails with a COTP or Setup Communication error** while the TCP connect is fine: wrong `Rack` / `Slot`, or the CPU does not accept that `ConnectionType` (`PG`, `OP`, `Basic`).
- **S7: `The S7 packet limits are unavailable …`** (`Unspecified`): the operation ran before the handshake wrote a session snapshot, i.e. the channel was created without the protocol's initializer. Use `S7Client.Create`.
- **FINS: `The FINS route is unavailable …`** (`Unspecified`): same cause — the node-address handshake has not run on this channel. Use `FinsClient.Create` and connect first.
- **FINS: reads work, then a reconnect makes them fail**: the CPU assigned a different node number at the second handshake. Read `plc.ClientNode` / `plc.ServerNode` after reconnects instead of assuming the numbers you configured.
- **FINS: an end code that looks like a success (`0x0040`)**: the CPU set a status flag; the masked code is zero, so the operation succeeded. The client already logged the raw value.
- **Odd string length on a word device**: MELSEC and FINS pad the encoded bytes with one `0x00` to keep whole words; a byte device (S7) writes exactly what you asked for.

## Not Yet Verified (P2-D16, P4-D20, P7-D17)

Nothing in these three packages has been checked against real hardware. The items below are inferred from manuals, published captures or community experience, and are tested against golden vectors only. Verify them on your own PLC or CPU before you rely on them.

MELSEC (P2-D16):

1. The format 4 response sum check covers the bytes from the frame identifier through ETX.
2. The format 5 error response's 2-byte end code is read as little-endian.

S7 (P4-D20):

1. `0x8104` meaning "PUT/GET not permitted" is community experience, not Siemens documentation — it is why the hint text is attached to that combination.
2. The write limit `PDU − 28` is derived from the header fields; Snap7 uses the more conservative `PDU − 35`.

FINS (P4-D20):

1. The write limit of 996 points is a conservative derivation from the 2012-byte frame ceiling.
2. The `EM` area codes (`0xA0` + group, `0x20` + group, groups 0–C) and the expansion into thirteen symbols.
3. The end-code masking rule (major `& 0x7F`, minor `& 0x3F`, masked zero is success).
4. What the CPU answers to a node-address request when you ask for a fixed client node — the client follows the response rather than your request.

Modbus (P7-D17): interoperation with an independent slave (pymodbus 3.16.1) passed on 2026-10-26 for every framing and transport, including RTU over UDP, devices without 0x16 and devices that accept only 0F and 10. Not yet checked:

1. Real devices and Modbus Poll / Modbus Slave.
2. RTU resynchronisation on a noisy line.
3. What station 0 and station 255 mean on a TCP gateway (vendor-specific).

## Protocol Author Guide

Use this section when you add a protocol package (for example `Junevy.Communication.<Name>`). Three implementations sit on the suite and each is the reference for one shape: MELSEC (word and bit devices, three transports), S7 (byte devices, one stream transport, a two-number address and a negotiated PDU), FINS (word devices plus native bits, one stream transport, a handshake that supplies routing).

### Package layout

```
Junevy.Communication.<Name>/
  <Name>AddressParser or device table   declares the devices (a PlcAddressParser<TDevice, TAddress>)
  <Name>Address.cs                      derives PlcAddress (device, number, bit, text)
  <Name>ClientOptions.cs                derives PlcClientOptions
  Protocol/                             frame encoding and decoding, with no channel dependency
  Framing/                              stream framing (length field or delimiter codec) when the transport is a stream
  <Name>ProtocolProfile.cs              declares the channel components per delivery, guards against pre-set ones
  <Name>ProtocolSettings.cs             immutable snapshot of the options the profile and hooks read
  <Name>Handshake.cs + <Name>Session.cs IConnectionInitializer plus the session snapshot, when a limit or a routing value comes from the link
  <Name>ReadProbe.cs                    the heartbeat probe
  <Name>Client.cs                       derives PlcClientBase<TAddress>; hooks and static factories
  <Name>StringExtensions.cs             S7 string / WString helpers (only when the PLC stores length-prefixed strings)
  DependencyInjection/                  config type (IPlcClientConfig), creator (IPlcClientCreator), AddX()
Junevy.Communication.<Name>.Tests/
  TestSupport/                          device adapter (PlcMemory or PlcByteMemory + DeviceSimulator), golden vectors
```

### Device table and address type

- A device implements `IPlcDeviceDefinition`: `Symbol`, `Unit`, `Radix` (10 or 16) and `MaxNumber`.
- `Unit` is `PlcAddressUnit.Bit`, `Word` or `Byte`, and it decides which hook family the pipeline calls and how the point limits count: `Word` addresses go through the word hooks (two bytes per point, little-end in MELSEC, big-endian in FINS), `Byte` addresses through the byte hooks, `Bit` devices through the bit hooks. A `Byte` device counts `PlcAccess.Bytes` in bytes; a `Word` device counts `PlcAccess.Words` in words and `PlcAccess.Bits` in points.
- Declare the table as an array and build the parser once: `new PlcAddressParser<TDevice, TAddress>(devices, (device, number, bit, text) => new TAddress(...), bitIndexRadix: 16)`. The parser is case-insensitive and picks the longest symbol (`ZR` before `Z`). `TryParse(text, out address, out error)` puts a readable message in `error` on failure, and the client reports it as `InvalidRequest`. Use `bitIndexRadix: 10` when the device's bit indices are decimal, as FINS writes them (`W10.03`).
- A device table cannot express an address with two numbers (`DB1.DBW12` = block 1, byte 12). S7 therefore hand-writes `S7AddressParser` instead of reusing the declarative one, and keeps the suite's message shapes: `The address is empty.` and `'<x>' is not a valid S7 address: <reason>.` (P4-D16).
- `PlcAddress.Device` is typed as `IPlcDeviceDefinition`. A derived address that needs the concrete device declares `public new McDevice Device => (McDevice)base.Device;`.

### Hooks

`PlcClientBase<TAddress>` declares its hooks `protected internal`. A protocol in another assembly overrides them with `protected override`.

| Hook | Shape |
|---|---|
| `TryParseAddress` (abstract) | `bool TryParseAddress(string address, out TAddress? parsed, out string? error)` |
| `IsReadOnly` (virtual, default `false`) | `bool (TAddress address)` — a write to a read-only address returns `InvalidRequest` before any point check; reads are not affected |
| `GetMaxReadPoints` / `GetMaxWritePoints` (abstract) | `int (TAddress address, PlcAccess access)` — the per-packet cap counted in the `access` unit |
| `BuildReadWordsRequest` (abstract) | `void (TAddress address, int offset, int count, IBufferWriter<byte> output)` |
| `ParseReadWordsResponse` (abstract) | `CommResult<byte[]> (int count, ReadOnlySpan<byte> frame)` (normalised bytes) |
| `BuildWriteWordsRequest` (abstract) | `void (TAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output)` |
| `BuildWriteBitsRequest` (abstract) | `void (TAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output)` |
| `ParseWriteResponse` (abstract) | `CommResult (ReadOnlySpan<byte> frame)` |
| `BuildReadBitsRequest` (virtual, default throws) | `void (TAddress address, int offset, int count, IBufferWriter<byte> output)` |
| `ParseReadBitsResponse` (virtual, default throws) | `CommResult<bool[]> (int count, ReadOnlySpan<byte> frame)` |
| `BuildReadBytesRequest` (virtual, default throws) | `void (TAddress address, int offset, int count, IBufferWriter<byte> output)` — offset and count in bytes |
| `ParseReadBytesResponse` (virtual, default throws) | `CommResult<byte[]> (int count, ReadOnlySpan<byte> frame)` — in device memory byte order |
| `BuildWriteBytesRequest` (virtual, default throws) | `void (TAddress address, int offset, ReadOnlySpan<byte> bytes, IBufferWriter<byte> output)` |
| `CanWriteBitsNatively` (virtual) | `bool (TAddress address)`; the default is `address.Unit == PlcAddressUnit.Bit`. Override to `true` when a word device takes a one-bit write (FINS), to `false` when it does not (MELSEC) |
| `ValidateRange` (virtual) | `CommResult (TAddress address, PlcAccess access, int count)`; returns `InvalidRequest` for requests the protocol cannot represent |

Only the word hooks plus `BuildWriteBitsRequest` and `ParseWriteResponse` are abstract, because every address-based protocol has them. The bit-read and byte hooks have default bodies that throw `InvalidOperationException`; the pipeline turns that into `Unspecified` and logs it at `Error`, so a protocol with no byte devices never implements the byte hooks and a caller still gets a result rather than an exception (P4-D5).

The `Build*` hooks return `void`, so they cannot report an error. Put representability checks (device number ranges, point limits per encoding, address-field overflow) in `ValidateRange`. The base class calls it before any frame is built. S7 and FINS each use it to reject an access whose end number passes the area's maximum, with the message shaped as `The access on '<x>' ends at byte N, beyond the address field limit 2097151.` (S7) or `... ends at word N, beyond the address field limit 65535.` (FINS).

### Protocol-specific requests

`SendProtocolRequestAsync<T>(operation, buildRequest, parseResponse, cancellationToken)` (protected) sends one packet for a function code the read and write entry points do not cover. It has the same guards, operation lock, retry rules and error mapping as the entry points. Modbus uses it for 0x16 and 0x17. A null delegate or a blank operation name throws synchronously, because it is a programming error.

### Handshake, session and negotiated limits

When the PLC supplies a value the packet limits depend on (S7's PDU size) or a routing value (FINS's node numbers), do not put it in the options:

1. Make an internal `XSession` holding one immutable `XSessionState` reference, read with `Volatile.Read` and replaced whole in `Volatile.Write` after each handshake. Never mutate a state a running operation may be reading.
2. Implement `IConnectionInitializer` in `XHandshake`, give it the settings snapshot and the same session object, and set `components.Initializer` in the profile.
3. The hooks read `session.Current` (or a wrapper that throws a clear `Unspecified`-bound message when it is still `BeforeHandshake`), so a channel created without the profile fails loudly instead of sending a malformed frame.
4. Expose the negotiated values read-only on the client (`NegotiatedPduSize`, `ClientNode`, `ServerNode`) and document that they are null or unset before the first connect.

For a request/response protocol whose frames carry no correlation id (S7, FINS over TCP), set `components.Correlation = CorrelationMode.Sequential`; the channel answers in send order. A protocol with an echo field (`KeyExtractor`) keeps the default.

### Transport profile and factories

- Declare the channel components for each transport in a profile. Use `PlcComponentWiring.Apply` — do not write the guard and the assignments yourself:

  ```csharp
  using System;
  using Junevy.Communication.Channels;
  using Junevy.Communication.Plc;

  internal static class MyProtocolProfile
  {
      public static TComponents Configure<TComponents>(TComponents components, MyProtocolSettings settings, MySession session, ChannelDelivery delivery)
          where TComponents : ChannelComponents
      {
          // Rules that depend on the transport are the protocol's own, and run before the wiring.
          if (delivery != ChannelDelivery.Stream)
              throw new ArgumentException("This protocol runs over a byte stream.", nameof(delivery));

          return PlcComponentWiring.Apply(
              components,
              owner: nameof(MyProtocolProfile),
              frameCodec: MyFraming.Create(),
              correlation: CorrelationMode.Sequential,
              keyExtractor: null,
              initializer: new MyHandshake(settings, session),
              healthProbeFactory: channel => new MyReadProbe(channel));
      }
  }
  ```

  `owner` only shows up in the exception message, so use the profile's own type name. `Apply` always owns `FrameCodec`, `Correlation`, `KeyExtractor`, `HealthProbe` and `HealthProbeFactory`: if the caller already set one, it throws `ArgumentException` with `ParamName` `"components"` instead of overriding. Pass `frameCodec: null` when the protocol has no framing (a datagram transport) and `keyExtractor: null` when correlation is not `Keyed`. `Initializer` belongs to the protocol only when you pass one; pass `initializer: null` to leave a caller-provided handshake hook alone. `ReconnectPolicy` is never touched.
- A stream transport that carries a length-prefixed frame declares it with `FrameCodecFactory.Create(new FramingOptions { ... })` and the offset/length/correction of that shell: S7 uses the TPKT length at offset 2 with correction ?4, FINS the 4-byte shell length at offset 4 with correction 0. Keep `MaxFrameLength` at the default (65536) unless the protocol proves a smaller ceiling.
- Header fields go through Core's `BigEndianFields` (`WriteUInt8/16/24/32`, `WriteBytes`, `TryTakeUInt8/16/24/32`, `TryTakeBytes`), not through a per-protocol helper. Reads return `false` and leave the span untouched on a short buffer, which is what a decoder needs so it can return a structural error instead of throwing.
- The value that pairs a reply with its request comes from a `SequenceCounter` on the type that encodes that header field, shared process-wide so requests and the heartbeat probe draw from one source. Keep one instance per width.
- Reject a delivery that cannot carry the protocol **before** calling `Apply`: S7 and FINS require `ChannelDelivery.Stream` and throw `ArgumentException` for datagrams, because the framing shell only makes sense on a byte stream. Rules that depend on the medium (a frame type that only exists on serial) also belong to the profile, for the same reason.
- Provide one creation entry per protocol: `Create(IChannelConfig transport, ...)`. Add an overload per built-in transport only as a shortcut to it. Create the channel with `PlcChannelBinding.CreateChannel(creator, name, transport, configureComponents, loggerFactory)`: the components come from the creator's `Delivery` and `Medium`, and a transport that is not built in supplies its own `IChannelCreator`, so the protocol package does not change (P25-D4).
- Take the options snapshot before creating the channel, and dispose the channel if the client constructor then throws — the channel has no other owner and the caller never receives it.
- Check the transport configuration's defaults (framing, correlation) and throw `ArgumentException` when they differ.
- A protocol that runs over one transport only (S7, FINS) gives its DI config no `Transport` switch: the config carries the `Tcp` section plus `Options`, and a null `Tcp` throws `ArgumentException`.

### Creator, DI and tests

- The creator's `ConfigType` is the exact config type. `Create` validates the configuration, throws `ArgumentException` when it is wrong, and never modifies the caller's object.
- `AddX()` calls `services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlcClientCreator, XClientCreator>())` and then `AddPlcClients()`.
- Test the frame encoding against the manual's golden vectors (`HexVector.ToBytes` in `Junevy.Communication.Testing`), in both directions where the manual gives both. Test the device adapter with `PlcMemory` and `DeviceSimulator`, and run the end-to-end matrix over every transport.
- Real-socket tests go in the project's `SocketTiming` collection so they do not run in parallel with each other; wait for conditions with `Polling.WaitUntilAsync(() => …)` or `Arrival.WithinAsync(task, ms)` from `Junevy.Communication.Testing`, never `Thread.Sleep` and never a hand-rolled `Task.WhenAny` + `Task.Delay` race.
- `DuplexClientChannel`'s `onOpened` callback is awaited before the connection completes. Start the simulator inside it without returning its task, or the test hangs.
- The simulator must reproduce the handshake, not only the memory: for S7 it answers COTP CR, then Setup Communication with a PDU size, then the read/write requests; for FINS it answers the node-address request before it accepts frames. A client that connects to a simulator which skips the negotiation cannot detect a broken snapshot.

### What you must write and what you do not

| Must write | Not needed (the suite provides it) |
|---|---|
| Device table and address type (or a hand-written parser when two numbers make the address) | Connection, reconnect, heartbeat scheduling, timeouts |
| The abstract hooks, plus the bit-read or byte hooks only if the device table has that unit, `CanWriteBitsNatively` when the default is wrong, and `ValidateRange` if the limits depend on the frame | Stream framing as such (you declare it in the profile), the byte/word/bit routing between hook families |
| Frame codecs and the framing shell, with golden-vector tests | Request serialisation, retries, batching, packet splitting, big-endian field helpers (`BigEndianFields`) |
| Handshake, session snapshot and read probe when the link negotiates anything | Address text normalisation, the operation lock, the disposed check |
| Transport profile and client factories (the profile itself is one `PlcComponentWiring.Apply` call plus its own delivery rule) | The component guard and the assignment order (`PlcComponentWiring`), the sequence counter (`SequenceCounter`) |
| Creator, config type and the `AddX()` extension | Logging, the registry, the factory |
| Device adapter, simulator and end-to-end tests | Transport adapters (TCP, UDP, serial) |

## Lessons From the P2 Friction Log

These are the traps the MELSEC implementation hit. The design document's section 20.2 has the full entries.

- `ConnectionStatistics` and `ConnectionStateChangedEventArgs` have internal constructors. A test double that implements `IClientChannel` needs reflection or a Channels test kit. Prefer `ScriptedClientChannel` (scripted replies) or `DuplexClientChannel` (an in-memory stream) from `Junevy.Communication.Testing`.
- `PlcAddress.Device` is the interface type, so a derived address exposes a shadowing `new` property. Callers that need the concrete device must cast.
- The `Build*` hooks cannot report errors. An ASCII decimal number above 999999 cannot be encoded, so the check belongs in `ValidateRange`, and the encoder keeps a matching guard.
- `IFrameCodecFactory` does not receive `FramingOptions.MaxFrameLength`. A custom codec factory must take the limit separately and keep it consistent with the channel's configuration.
- `ChannelComponents` silently overrides a same-named transport configuration setting. Guard the profile (see above).
- `PassthroughFrameEncoder` is internal to Channels. Borrow a Raw-mode codec factory from `FrameCodecFactory.Create(new FramingOptions())` when you need pass-through encoding.
- A format 5 decoder must skip escaped `10H 10H` pairs before it finds an unescaped DLE ETX. Channels has no forward byte reader, so the current decoder copies its window and rescans it on each arrival.
- `SequenceFactory` (Testing) builds segmented `ReadOnlySequence<byte>` values. Use it for decoder tests that span buffer boundaries.
- `DuplexClientChannel`'s `onOpened` is awaited. Returning a long-running task from it hangs the connect (see above).

## Lessons From the P4 Friction Log

These are the traps S7 and FINS hit on top of the suite. Design document section 20.5 and plan sections 20.2–20.3 have the full entries (F1–F11).

- A declarative device table fits one number per address. `DB1.DBW12` has two, and the width letter decides whether a bit index is mandatory or forbidden, so S7 hand-writes `S7AddressParser` and keeps the suite's message shapes. FINS (18 symbols, EM groups included) needed no such workaround — the model held for the second protocol, so hand-writing a parser is the exception, not a sign the table is wrong (F1).
- `PlcLayout` and `PlcBitPacking` are internal to Plc, so a protocol writes its own round-up from bytes to bits (three lines in `S7VarItem`). A new IVT is not an option: the architecture test fixes the production-to-production IVT count at 2 (F2).
- Process-wide sequence counters used to exist three times: `McSerialNumbers`, `S7PduReferences`, `FinsSessionIds` — 13 to 14 lines each, identical skeleton, different width. The duplicate scanner did not see them because the comparable line count was under 6, so treat "the scanner is quiet" as evidence of nothing (F3). **Resolved in P4.5**: all three now use Core's `SequenceCounter`, so a new protocol takes `private static readonly SequenceCounter Requests = new SequenceCounter();` on the type that encodes that header field and calls `NextUInt16()` or `NextByte()`. Keep it process-wide so the client's requests and its heartbeat probe share one source; one instance per width.
- The `RequireUnset` component guard was a third copy per protocol, and it is what pushed the production duplicate-window metric above the baseline. Do not "fix" duplication by rewriting six `if` statements as a data-driven loop: that hides the duplication from the metric while the logic stays duplicated, which is rewriting for the scanner (F4). **Resolved in P4.5**: the guard and the assignments moved into one shared call — `PlcComponentWiring.Apply(components, owner: nameof(XProtocolProfile), frameCodec: …, correlation: …, keyExtractor: …, initializer: …, healthProbeFactory: …)`. It refuses to overwrite anything the caller set and assigns only after every guard passes.
- Hooks are `protected internal`, and the `internal` half does not reach a protocol's test assembly. A protocol test cannot call `GetMaxReadPoints`, `BuildReadBytesRequest` or `ValidateRange` directly; it must drive a connected client with a canned CPU. S7 moved its PDU arithmetic into `S7SessionState` to make the limits assertable; FINS has constant limits and could not, so 16 of its 22 hook tests drag a pipeline plus a simulator in front of a pure arithmetic expectation (F5).
- `TcpClientChannel` validates its configuration in the constructor, so `Port = 0` throws before a `Create`-guard test reaches its assertion. Give the test's transport the protocol's standard port (102 for S7, 9600 for FINS) and keep the null guards in `Create` ahead of the channel creation, or the guard is unreachable (F6).
- `PlcMemory` in `Junevy.Communication.Testing` stores words. An S7 simulator that needs byte blocks, "block does not exist" and "past the end of the block" used to carry its own `Dictionary<(S7Area, int), byte[]>`; a FINS bit write lives inside a word, so its simulator routed bits through the word store (F7). **Resolved in P4.6**: `PlcByteMemory` is a thread-safe byte store keyed by (area name, block number) — `Define(area, block, length)` creates a zeroed block and hands back the storage, `Block(area, block)` returns it for pre-setting and inspection, and `TryRead` / `TryWrite` return `PlcByteAccess.Ok` / `NoSuchBlock` / `BeyondEnd`. A failed write leaves the block byte-for-byte unchanged. Use it for byte-addressed memory; keep `PlcMemory` for the word-granular API. Translating `NoSuchBlock` / `BeyondEnd` into the protocol's own error code stays in the simulator — the S7 one maps them to 0x0A and 0x05, FINS maps its own. Bit layout is the protocol's business: FINS puts each word's 16 bits across two bytes and lives in its own `FinsSimulatorMemory`, so expect to write that layout once rather than guess at it.
- Big-endian field helpers (`WriteRaw`, `TryTakeUInt8/16`) used to exist once per protocol, and that pair of files was the largest single contributor to the duplicate metric. There was no Core utility for "take a field from the head of a span and fail rather than throw" — `ByteTransform` throws on a short buffer, which violates the decoder rule that structural errors come back as results (F8). **Resolved in P4.5**: Core's `BigEndianFields` writes and takes 1/2/3/4-byte big-endian fields; `TryTake*` returns `false` and leaves the span untouched when there are not enough bytes, so a decoder reports the structural error through its own result. Pass `frameCodec`-style widths from `BigEndianFields.UInt16Bytes` rather than redeclaring them.
- The whole profile file was the same shape across protocols: guard set components, refuse datagrams, declare framing, set `CorrelationMode.Sequential`, hang the initializer and the probe factory. So was the framing one-liner (`FrameCodecFactory.Create(new FramingOptions { … })`). Three protocols wrote it three times (F9). **Resolved in P4.5**: `PlcComponentWiring.Apply` (see F4 above) does the guard and the assignment; a new profile keeps only what is genuinely its own — the delivery or medium rule that runs *before* the call, and the field values themselves.
- End-to-end failure tests for two protocols were the same TCP scaffolding: `ScriptedTcpServer.Start`, a `TaskCompletionSource` with `RunContinuationsAsynchronously`, `ReconnectOptions { Enabled = true, Interval = 100 }`, `HeartbeatOptions { Enabled = true, Interval = 100, Timeout = 200, MaxFailures = 2 }`. The Testing library had the server and the device simulator but no minimal reconnectable/heartbeatable TCP config and no arrival-with-timeout helper (F10). **Resolved in P4.6**: `LoopbackTcp.Client(port)`, `LoopbackTcp.Reconnecting(port, ...)` and `LoopbackTcp.WithHeartbeat(port, ...)` are three separate static factories (they do not chain) that return a fresh `TcpClientChannelConfig` on `127.0.0.1` with nothing, reconnect, or heartbeat plus reconnect switched on, and `Arrival.WithinAsync(task, ms)` fails the test when the awaited thing has not arrived in time. Use both instead of re-spelling the options objects. The protocol-specific parts — the simulator, the handshake counter, the byte-by-byte assertions — still belong in the protocol's test project.
- `CommResult<T>.Data` used to be declared `T?` with no nullability link to `IsSuccess`, so `return result.IsSuccess ? result.Data : fallback;` was a CS8603 warning as soon as `T` is a reference type (`string`, `byte[]`) — it was silent for value types, because `T?` over an unconstrained type parameter does not become `Nullable<T>`. Writing `result.Data ?? fallback` was the workaround (F11). **Resolved in P4.5**: `IsSuccess` carries `[MemberNotNullWhen(true, nameof(Data))]` and `[MemberNotNullWhen(false, nameof(ErrorMessage))]`, so inside `if (result.IsSuccess)` the compiler already knows `Data` is not null and inside the negative branch it knows `ErrorMessage` is not null. Write the plain `result.Data`; do not add a coalescing operator or a null-forgiving `!`. The invariant is backed by `CommResult<T>.Success` rejecting a null `data`, so a nullable `T` is not the intended use — report a "successful but empty" outcome with `Fail` instead.
- When you document a protocol, compile the samples: extract every `csharp` fenced block from the package README **and** this skill into a throwaway project (`ImplicitUsings=disable`, `Nullable=enable`, `TreatWarningsAsErrors=true`, both target frameworks) instead of transcribing them. That is what caught the non-existent `AddFins()` (the method is `AddOmronFins()`), the missing `using Junevy.Communication.Plc;` for the typed readers, and the `Data` nullability above.

When you add the fourth protocol, none of F3–F10 reappear: they are shared parts now (`PlcComponentWiring`, `SequenceCounter`, `BigEndianFields`, `PlcByteMemory`, `LoopbackTcp`, `Arrival.WithinAsync`), and the guide above shows the one-line call for each. F5 (hooks you cannot call directly from a protocol test) is the exception and is still yours to work around — plan for that one rather than treating it as a mistake in your design.

## Lessons From the P7 Friction Log

- A read-only area needs its own hook. `ValidateRange` does not know the direction of an access, so it cannot refuse writes to an input register; `IsReadOnly` does (P7-S1).
- A protocol-specific function code (Modbus 0x16 and 0x17) needs the suite's operation lock and retries. Copying the packet loop would duplicate the retry rules; `SendProtocolRequestAsync` reuses them (P7-S2).
- A declarative address parser cannot express a station prefix (`s=n;`) or the one-based Modicon form. The Modbus parser is protocol-specific and internal; it keeps the suite's whitespace and case rules (P7-F-A1).
- An `IClientChannel` test double was duplicated per test project. It now lives in the shared testing library as `ScriptedClientChannel` (scripted replies or a responder, recorded requests, simulated disconnects); use it for hook tests instead of writing another one (P7-F-A2, review R7).
- The shared device simulator answers synchronously. A late TCP reply needs a stream wrapper that writes in the background, so the request loop keeps running (P7-F-A3).
