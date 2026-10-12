# Junevy.Communication.Siemens

Siemens S7 client: the classic S7comm PDU carried over ISO-on-TCP (RFC 1006, port 102), built on the protocol-neutral PLC suite. It connects with a two-step handshake (COTP CR/CC, then Setup Communication), reads and writes the `DB`, `M`, `I` and `Q` areas by address with the typed API, splits large operations into packets, and gets reconnect, heartbeat, timeouts and retries from the channel family.

> **Preview and validation status**: the package ships as `1.0.0-preview.1`. The frames were assembled from public protocol descriptions and are checked against the golden vectors of the P4 plan. It has **not been verified against a real PLC**; see [Not Yet Verified](#not-yet-verified-p4-d20).

## Target Frameworks

`net472` and `net8.0`.

## Installation

```text
dotnet add package Junevy.Communication.Siemens --prerelease
```

The package depends on `Junevy.Communication.Plc` and `Junevy.Communication.Tcp`.

## Scope

- **CPUs**: S7-1200 and S7-1500. S7-300/400, S7-200 SMART and LOGO! differ only in connection parameters, but this package does not claim support for them because they were not tested (P4-D1).
- **Transport**: ISO-on-TCP only, port 102. There is no UDP, serial or S7comm-plus (symbolic) transport; `S7Client.Create` throws `NotSupportedException` for any other transport configuration type (P4-D2).
- **Services**: COTP CR/CC and DT, Setup Communication (0xF0), Read Var (0x04) and Write Var (0x05). Each request carries one item.
- **Areas**: `DB` (0x84), `M` (0x83), `I` (0x81), `Q` (0x82), all byte-addressed.
- **Not supported**: S7comm-plus and symbolic access, optimized data blocks, the timer and counter areas, multiple items per request, clock read/set, and uploading or downloading blocks.

### PLC-side settings (TIA Portal)

Three settings on the CPU decide whether the first connection works:

1. **Allow PUT/GET**: CPU properties → *Connections* → security settings → *Permit access with PUT/GET communication functions*. Without it the CPU answers with error `0x8104`.
2. **De-optimise the data blocks**: each data block you address needs *Optimized block access* unchecked. Absolute addresses such as `DB1.DBW12` do not exist for optimized blocks.
3. **Open port 102** on the CPU and on any firewall between the PC and the CPU.

## Quick Start

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens;
using Junevy.Communication.Tcp;

public static class S7TcpExample
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = new S7ClientOptions { Rack = 0, Slot = 1 };
        await using S7Client plc = S7Client.Create(new TcpClientChannelConfig { Host = "192.168.1.20", Port = 102 }, options);

        CommResult connect = await plc.ConnectAsync(cancellationToken);
        if (!connect.IsSuccess)
            throw new InvalidOperationException($"Connect failed: {connect.ErrorKind}. {connect.ErrorMessage}");

        CommResult write = await plc.WriteInt16Async("DB1.DBW12", 1234, cancellationToken);
        if (!write.IsSuccess)
            throw new InvalidOperationException($"DB1.DBW12 could not be written: {write.ErrorKind}. {write.ErrorMessage}");

        CommResult<short> level = await plc.ReadInt16Async("DB1.DBW12", cancellationToken);
        if (!level.IsSuccess)
            throw new InvalidOperationException($"DB1.DBW12 could not be read: {level.ErrorKind}. {level.ErrorMessage}");

        CommResult<byte[]> flags = await plc.ReadBytesAsync("MB0", 16, cancellationToken);
        if (!flags.IsSuccess)
            throw new InvalidOperationException($"MB0 could not be read: {flags.ErrorKind}. {flags.ErrorMessage}");
    }
}
```

The typed API is the suite's: `ReadInt16Async`, `WriteFloatAsync`, `ReadBytesAsync` and the rest are documented in the `Junevy.Communication.Plc` README.

## Options

`S7ClientOptions` derives from `PlcClientOptions`. The client validates the options and takes a snapshot when it is created, so later changes to the object have no effect.

| Property | Default | Meaning |
|---|---|---|
| `Rack` | `0` | Rack number, `0`–`7`. Part of the remote TSAP |
| `Slot` | `1` | Slot number, `0`–`31`. S7-300/400 CPUs sit in slot 1, S7-1200/1500 accept `0` or `1` |
| `ConnectionType` | `PG` | `PG` (programmer), `OP` (operator) or `Basic`. Some CPUs only accept one kind of connection, which is the usual reason for a device-side error during the handshake |
| `DataFormat` | `null` (word order `ABCD`, ASCII strings) | Word order and string encoding of the typed API |
| `RetryCount` | `0` | Inherited. Retries after a retryable failure |
| `RetryInterval` | `1000` ms | Inherited. How long to wait for the link before a retry |
| `AllowReadModifyWriteBits` | `false` | Inherited, but S7 never needs it: every area is byte-addressed and bit writes are native, so the suite never reads a word back |

Remote TSAP = `(ConnectionType << 8) | (Rack << 5) | Slot`, local TSAP = `0x0100` (the Snap7 convention, P4-D19). Out-of-range values throw `ArgumentOutOfRangeException` from `Create`.

`S7Client.NegotiatedPduSize` reports the PDU length the CPU agreed to (`null` before the first successful handshake, and after a handshake that has not run yet).

## Addresses

Case-insensitive, with an optional `%` prefix. All numbers are decimal. The width letter only writes the address the way the TIA Portal shows it; it does not limit the length of a read or write, which is decided by the method you call (P4-D16).

| Area | Bit | Byte | Word | Double word |
|---|---|---|---|---|
| Data block | `DB1.DBX12.3` | `DB1.DBB12` | `DB1.DBW12` | `DB1.DBD12` |
| Merkers | `M12.3` | `MB12` | `MW12` | `MD12` |
| Inputs | `I12.3` | `IB12` | `IW12` | `ID12` |
| Outputs | `Q12.3` | `QB12` | `QW12` | `QD12` |

- `X` requires a bit index; `B`, `W` and `D` reject one. `M12.3` and `M12` are both valid, because the area letter there is followed directly by the number.
- Ranges: data block `1`–`65535`, byte number `0`–`2097151` (the largest byte the 24-bit bit-address field can hold), bit index `0`–`7`.
- An unparseable address is `InvalidRequest`, with the parser's message, for example `'DB0.DBW0' is not a valid S7 address: the data block number must be 1–65535.`
- A request whose last byte passes `2097151` is rejected before anything is sent: `InvalidRequest` with `The access on 'MD2097151' ends at byte 2097153, beyond the address field limit 2097151.`
- Every area is a byte device (`PlcAddressUnit.Byte`): word access treats one word as the two consecutive bytes that start at the address, and bit access addresses the bits inside a byte (bit 0 is the lowest bit).

## Data Format and Strings

The normalized layout is the CPU's own byte order, so `ReadAsync` and `WriteAsync` give you the bytes as they sit in the memory area. S7-1200/1500 are big-endian: a 16-bit value is `AB` and a 32-bit value `ABCD`, which is the default `WordOrder.ABCD` (P4-D11). Set `DataFormat = new PlcDataFormat(WordOrder.DCBA, Encoding.UTF8)` to change it.

Strings have two routes:

- **Raw character runs** use the suite's `ReadStringAsync(address, byteLength)` and `WriteStringAsync`. On S7 the read is byte-exact, so a trailing byte of the field never turns into a stray character, and `WriteStringAsync(address, value, byteLength)` writes a fixed-length, zero-padded field.
- **The `String` and `WString` PLC types** carry a length header: `String` uses 2 bytes (declared maximum length, then actual length), `WString` uses 4 bytes (both fields 16-bit big-endian). The `S7Client` extension methods handle that header for you:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Siemens;

public static class S7StringExample
{
    public static async Task<string> ReadRecipeNameAsync(S7Client plc, CancellationToken cancellationToken = default)
    {
        CommResult<string> name = await plc.ReadS7StringAsync("DB1.DBB100", 20, cancellationToken);
        return name.Data ?? string.Empty;
    }

    public static Task<CommResult> WriteRecipeNameAsync(S7Client plc, string value, CancellationToken cancellationToken = default)
        => plc.WriteS7StringAsync("DB1.DBB100", value, 20, cancellationToken);

    public static Task<CommResult> WriteLabelAsync(S7Client plc, string value, CancellationToken cancellationToken = default)
        => plc.WriteS7WStringAsync("DB5.DBB0", value, 120, cancellationToken);
}
```

| Method | Type | `maxLength` | Characters |
|---|---|---|---|
| `ReadS7StringAsync` / `WriteS7StringAsync` | `String` | `1`–`254` | `PlcDataFormat.StringEncoding` (ASCII by default) |
| `ReadS7WStringAsync` / `WriteS7WStringAsync` | `WString` | `1`–`16382` | always UTF-16 big-endian |

A read asks for header plus the declared maximum and decodes the actual length from the header. A write sends header plus the actual characters, so the remaining bytes of the field keep their old values. If the header's actual length exceeds the declared maximum or the `maxLength` you passed, the reply is `ProtocolViolation`. A value that does not fit, or a `maxLength` outside the range above, is `InvalidRequest` and nothing is sent.

## Limits per Request

The CPU answers the Setup Communication request with its PDU size (this client asks for 960; S7-1200 usually answers 240, S7-1500 960). The packet limits follow from it (P4-D12):

| Negotiated PDU | Read bytes | Write bytes | Read words | Write words | Bits |
|---|---|---|---|---|---|
| 240 | 222 | 212 | 111 | 106 | 1 per request |
| 960 | 942 | 932 | 471 | 466 | 1 per request |

Larger operations are split into packets automatically, and each packet is retried on its own when `RetryCount` is greater than zero. The read header costs 18 bytes and the write header 28 bytes, which is where these numbers come from.

## Handshake, Heartbeat and Reconnect

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens;
using Junevy.Communication.Tcp;

public static class S7ResilienceExample
{
    public static S7Client Create()
    {
        var transport = new TcpClientChannelConfig
        {
            Host = "192.168.1.20",
            Port = 102,
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 1000 },
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 2000, MaxFailures = 3 },
        };
        return S7Client.Create(transport, new S7ClientOptions { Rack = 0, Slot = 1 });
    }
}
```

- The handshake runs on every connect and on every reconnect: COTP CR → CC, then Setup Communication → the negotiated PDU size. The result is stored as one immutable session snapshot that the packet limits read from (P4-D9).
- Replies are matched in send order (`CorrelationMode.Sequential`): the handshake frames have no correlation key, and the PDU reference is verified by the protocol layer instead (P4-D8).
- The heartbeat reads one byte at `MB0`. Any structurally valid answer counts as healthy, including one that carries an error code — a CPU that replies has a live link and protocol stack (P4-D15). Heartbeat and reconnect are off by default.

## Errors

Operations return a `CommResult`; they do not throw for device errors, timeouts or disconnects.

| Situation | `ErrorKind` | `ProtocolErrorCode` | Message |
|---|---|---|---|
| Error class or error code in the S7 header | `RemoteError` | `class << 8 \| code` | `S7 error class 0x81 code 0x04.` |
| Item return code not `0xFF` | `RemoteError` | the return code | `S7 item return code 0x05 (invalid address).` |
| Reply with a different PDU reference, or a malformed frame | `ProtocolViolation` | – | describes the field |
| Address cannot be parsed, or the range is not representable | `InvalidRequest` | – | see [Addresses](#addresses) |

The names in the item-return-code message come from the code table only: `0x01` hardware fault, `0x03` accessing the object not allowed, `0x05` invalid address, `0x06` data type not supported, `0x07` data type inconsistent, `0x0A` object does not exist. Error classes: `0x81` application relationship, `0x82` object definition, `0x83` no resources available, `0x84` error on service processing, `0x85` error on supplies, `0x87` access error. Any other kind is described in the `Junevy.Communication.Plc` README.

`0x8104` and the item return codes `0x03`, `0x05` and `0x0A` append this hint, because they are what the CPU answers when the two PLC-side settings above are missing:

> `Check that PUT/GET access is permitted in the CPU protection settings and that the data block does not use optimized block access.`

## Dependency Injection

```csharp
using System;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens;
using Junevy.Communication.Siemens.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class S7DependencyInjectionExample
{
    public static IServiceCollection Register(IServiceCollection services)
        => services.AddSiemensS7();

    public static IPlcClient Create(IServiceProvider provider, IConfiguration configuration)
    {
        S7ClientConfig config = configuration.GetSection("S7").Get<S7ClientConfig>()
            ?? throw new InvalidOperationException("The S7 section is missing.");
        return provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line-1", config);
    }
}
```

`AddSiemensS7()` registers the S7 creator and the `PlcClientFactory` singleton, and can be called more than once. There is no `Transport` switch because S7 only runs on ISO-on-TCP; a null `Tcp` section makes the creator throw `ArgumentException`. `PlcDataFormat` cannot be built from JSON (it has no parameterless constructor), so set it in code:

```json
{
  "S7": {
    "Tcp": { "Host": "192.168.1.20", "Port": 102 },
    "Options": { "Rack": 0, "Slot": 1, "ConnectionType": "PG" }
  }
}
```

## Not Yet Verified (P4-D20)

These rules are derived from the frame layout or from community experience, not from a CPU. They are covered by tests against the plan's golden vectors only:

1. **`0x8104` means "PUT/GET not permitted"**: widely reported, not documented by Siemens. The hint text is attached to that combination because of that report.
2. **The write limit `PDU − 28`**: derived from the fixed header and item-header fields. Snap7 uses the more conservative `PDU − 35`.

Everything else in this package is a claim about unverified behaviour on real hardware as well: the negotiation values CPUs answer with, the per-CPU area sizes, and the acceptability of `PG` / `OP` / `Basic` connections on a given CPU. Check them on your own hardware before production use.

## Not in P4

S7comm-plus and symbolic access, optimized blocks, the timer and counter areas, other CPU families, multiple items per request, clock services, per-call data format overrides, and a public operation-lock API.
