# Junevy.Communication.Omron

Omron FINS client: the FINS frame carried over FINS/TCP (port 9600), built on the protocol-neutral PLC suite. It takes its node addresses from a FINS/TCP node-address handshake, reads and writes the `CIO`, `W`, `H`, `A`, `D` and `EM` areas by address with the typed API, splits large operations into packets, and gets reconnect, heartbeat, timeouts and retries from the channel family.

> **Preview and validation status**: the package ships as `1.0.0-preview.1`. The frames were assembled from public protocol descriptions and are checked against the golden vectors of the P4 plan. It has **not been verified against a real CPU**; see [Not Yet Verified](#not-yet-verified-p4-d20).

## Target Frameworks

`net472` and `net8.0`.

## Installation

```text
dotnet add package Junevy.Communication.Omron --prerelease
```

The package depends on `Junevy.Communication.Plc` and `Junevy.Communication.Tcp`.

## Scope

- **Transport**: FINS over TCP only, port 9600. FINS/UDP and the Host Link serial protocols are not implemented, and `FinsClient.Create` throws `NotSupportedException` for any other transport configuration type (P4-D1, P4-D2).
- **Commands**: the FINS/TCP node-address request and response (shell commands 0 and 1), memory area read (0x0101) and memory area write (0x0102). Each request carries one area.
- **Areas**: `CIO`, `W`, `H`, `A`, `D` and the `EM` bank groups `E0_`–`EC_`, all word-addressed. Each area has a separate word and bit area code, so bit writes are native.
- **Not supported**: FINS/UDP, Host Link, the timer, counter and task areas, symbolic (variable-name) access on NJ/NX CPUs, multiple blocks per frame, and system and network management commands.

### PLC-side settings

1. **FINS/TCP is enabled on the Ethernet port** of the CPU (NJ/NX and CP series need a TCP unit service configured; CJ/CS use the built-in FINS port). The TCP port used by this client is **9600**.
2. **Node addresses.** The client sends a FINS/TCP node-address request when it connects. The CPU answers with its own node number and, if you asked for one, assigns the client's node number. Set `ClientNode` to `0` to let the CPU assign it, or to a fixed number in `1`–`254`. `255` is the FINS broadcast address and is rejected.
3. **Same network.** `DestinationNetwork` (`DNA`) and `DestinationUnit` (`DA2`) default to `0`, which is a directly connected CPU on the local network. `SNA` and `SA2` are always sent as `0`. A CPU behind a router or on another network needs those two values set to match its configuration.

## Quick Start

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

public static class FinsTcpExample
{
    public static async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var options = new FinsClientOptions { ClientNode = 0 };
        await using FinsClient plc = FinsClient.Create(new TcpClientChannelConfig { Host = "192.168.1.30", Port = 9600 }, options);

        CommResult connect = await plc.ConnectAsync(cancellationToken);
        if (!connect.IsSuccess)
            throw new InvalidOperationException($"Connect failed: {connect.ErrorKind}. {connect.ErrorMessage}");

        CommResult write = await plc.WriteInt16Async("D100", 1234, cancellationToken);
        if (!write.IsSuccess)
            throw new InvalidOperationException($"D100 could not be written: {write.ErrorKind}. {write.ErrorMessage}");

        CommResult<short> level = await plc.ReadInt16Async("D100", cancellationToken);
        if (!level.IsSuccess)
            throw new InvalidOperationException($"D100 could not be read: {level.ErrorKind}. {level.ErrorMessage}");

        CommResult<bool> running = await plc.ReadBoolAsync("CIO0.0", cancellationToken);
        if (!running.IsSuccess)
            throw new InvalidOperationException($"CIO0.0 could not be read: {running.ErrorKind}. {running.ErrorMessage}");

        byte? serverNode = plc.ServerNode;
    }
}
```

The typed API is the suite's: `ReadInt16Async`, `WriteFloatAsync`, `ReadStringAsync` and the others are documented in the `Junevy.Communication.Plc` README.

## Options

`FinsClientOptions` derives from `PlcClientOptions`. The client validates the options and takes a snapshot when it is created, so later changes to the object have no effect.

| Property | Default | Meaning |
|---|---|---|
| `ClientNode` | `0` | Requested client node number (`SA1`). `0` lets the handshake assign one, `1`–`254` is a fixed number, `255` throws `ArgumentOutOfRangeException` |
| `DestinationNetwork` | `0` | `DNA`, the network number of the CPU |
| `DestinationUnit` | `0` | `DA2`, the unit number of the CPU |
| `DataFormat` | `null` (word order `CDAB`, ASCII strings) | Word order and string encoding of the typed API |
| `RetryCount` | `0` | Inherited. Retries after a retryable failure |
| `RetryInterval` | `1000` ms | Inherited. How long to wait for the link before a retry |
| `AllowReadModifyWriteBits` | `false` | Inherited, but FINS never needs it: bit writes use the bit area code and are native, so the suite never reads a word back |

`FinsClient.ClientNode` and `FinsClient.ServerNode` report the node numbers of the most recent handshake and are `null` before the client has connected. When you ask for a fixed node number, the CPU can still hand out a different one, and the client then uses the number from the response (P4-D20 ⑥) — which is also why reconnects re-run the handshake.

## Addresses

Format `<device><number>[.<bit>]`, case-insensitive. Both the number and the bit index are **decimal** (P4-D16): `D100`, `d100`, `CIO0.0`, `W10.03`, `E3_200.15`.

| Device | Meaning | Word area code | Bit area code |
|---|---|---|---|
| `CIO` | Peripheral input and output | `0xB0` | `0x30` |
| `W` | Work memory | `0xB1` | `0x31` |
| `H` | Common memory | `0xB2` | `0x32` |
| `A` | Auxiliary memory | `0xB3` | `0x33` |
| `D` | Data memory | `0x82` | `0x02` |
| `E0_` – `EC_` | EM storage area, bank group 0–C | `0xA0` + group | `0x20` + group |

- Every device is a word device (`PlcAddressUnit.Word`): the number is a word, and one word holds 16 points. `W10.03` is bit 3 of `W10`; a bit index of `16` or more is rejected.
- The word range the parser accepts is `0`–`65535`, the full width of the two-byte address field. The area that a real CPU actually has is smaller, and going past it is answered by the CPU with an end code rather than by the client.
- Writing several bits starting at a bit address continues into the next word: `WriteBitsAsync("W10.15", …)` puts the second value into `W11.0`.
- Reading bits of a word area reads the covering words and selects the bits; the width letters and area codes never change the number you write.
- An unparseable address is `InvalidRequest`, for example `'W10.0A' is not a valid decimal bit index.` or `The bit index in address 'W10.16' must be between 0 and 15.`

## Data Format and Strings

FINS words are big-endian inside the word, and a 32-bit or 64-bit value puts the low word at the lower address, which the suite expresses as `CDAB`; that is the default (P4-D11). The normalized layout of `ReadAsync` and `WriteAsync` is the CPU's own byte order, so bytes come out high-byte-first per word. Set `DataFormat = new PlcDataFormat(WordOrder.ABCD, Encoding.UTF8)` if your block uses another order.

Strings use the suite's `ReadStringAsync` / `WriteStringAsync` (FINS has no string data type with a length header). Two FINS-specific details:

- A character occupies the high byte of a word, so `ReadStringAsync("D100", 8)` returns the text in the order the CPU shows it.
- `ReadByteAsync("D100")` on a word area reads the covering word and returns its **first byte in the normalized layout, which is the high byte of the word** — not the low byte as on MELSEC.

## Limits per Request

| Request | Points per request |
|---|---|
| Read (words, bits, or the words covering a byte range) | 999 |
| Write (words, bits, or whole words of a byte range) | 996 |

These are fixed values that follow from the 2012-byte frame limit, and they do not depend on the CPU model (P4-D12). Larger operations are split into packets automatically, and each packet is retried on its own when `RetryCount` is greater than zero.

## Handshake, Heartbeat and Reconnect

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Omron;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;

public static class FinsResilienceExample
{
    public static FinsClient Create()
    {
        var transport = new TcpClientChannelConfig
        {
            Host = "192.168.1.30",
            Port = 9600,
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 1000 },
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 2000, MaxFailures = 3 },
        };
        return FinsClient.Create(transport, new FinsClientOptions { ClientNode = 0 });
    }
}
```

- The handshake runs on every connect and on every reconnect: one FINS/TCP node-address request, and the two node numbers in the response are combined with `DestinationNetwork` and `DestinationUnit` into the route that the data frames use (P4-D9, P4-D18). The route is stored as one immutable session snapshot.
- Replies are matched in send order (`CorrelationMode.Sequential`): the node-address frames have no correlation key, and the session ID and command echo of the data frames are verified by the protocol layer instead (P4-D8).
- The heartbeat reads one word at `D0`. Any structurally valid answer counts as healthy, including one that carries an end code — a CPU that replies has a live link and protocol stack (P4-D15). Heartbeat and reconnect are off by default.

## Errors

Operations return a `CommResult`; they do not throw for device errors, timeouts or disconnects.

| Situation | `ErrorKind` | `ProtocolErrorCode` | Message |
|---|---|---|---|
| End code in a FINS response | `RemoteError` | the masked end code | `FINS end code 0x1103.` |
| Error field of the FINS/TCP shell | `RemoteError` | the shell error | `FINS/TCP error 0x00000002.` |
| Reply with a different session ID, a wrong command echo, or a malformed frame | `ProtocolViolation` | – | describes the field |
| Address cannot be parsed, or the range is not representable | `InvalidRequest` | – | see [Addresses](#addresses) |

Before an end code is judged, the status flags are masked out: the major byte keeps its low seven bits (`& 0x7F`) and the minor byte its low six bits (`& 0x3F`). A masked value of zero is success — a CPU that sets, say, `0x0040` has completed the command — and the client logs a debug line with the raw code. `ProtocolErrorCode` always carries the masked code (P4-D14).

The package deliberately has no table that explains FINS end codes: their meanings depend on the CPU model and unit, so look the code up in the Omron FINS manual. Any other `ErrorKind` is described in the `Junevy.Communication.Plc` README.

## Dependency Injection

```csharp
using System;
using Junevy.Communication.Omron.DependencyInjection;
using Junevy.Communication.Plc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class FinsDependencyInjectionExample
{
    public static IServiceCollection Register(IServiceCollection services)
        => services.AddOmronFins();

    public static IPlcClient Create(IServiceProvider provider, IConfiguration configuration)
    {
        FinsClientConfig config = configuration.GetSection("Omron").Get<FinsClientConfig>()
            ?? throw new InvalidOperationException("The Omron section is missing.");
        return provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line-1", config);
    }
}
```

`AddOmronFins()` registers the FINS creator and the `PlcClientFactory` singleton, and can be called more than once. There is no `Transport` switch because FINS only runs over TCP here; a null `Tcp` section makes the creator throw `ArgumentException`. `PlcDataFormat` cannot be built from JSON (it has no parameterless constructor), so set it in code:

```json
{
  "Omron": {
    "Tcp": { "Host": "192.168.1.30", "Port": 9600 },
    "Options": { "ClientNode": 0, "DestinationNetwork": 0, "DestinationUnit": 0 }
  }
}
```

## Not Yet Verified (P4-D20)

These rules are inferred from the frame layout or from community experience, not from a CPU. They are covered by tests against the plan's golden vectors only:

1. **The write limit of 996 points**: derived from the 2012-byte frame ceiling with a conservative margin; the manual states the read limit of 999 but not a symmetric write limit.
2. **The `EM` area codes** (`0xA0` + group for words, `0x20` + group for bits, groups 0–C): the expansion of one area into thirteen device symbols is not documented per CPU model.
3. **The end-code masking rule** (major `& 0x7F`, minor `& 0x3F`, masked zero is success).
4. **The handshake response when a fixed client node is requested**: whether the CPU echoes your number or hands out its own, and which one the route must then use. This client follows the response.

Everything else in this package is a claim about unverified behaviour on real hardware as well: the area sizes of a specific CPU unit, and whether the CPU's unit service accepts FINS/TCP from your node number. Check them on your own hardware before production use.

## Not in P4

FINS/UDP, Host Link over serial, the timer, counter and task areas, symbolic (variable-name) access on NJ/NX CPUs, multiple blocks per frame, CPU model and mode commands, per-call data format overrides, and a public operation-lock API.
