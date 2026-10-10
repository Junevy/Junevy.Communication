# Junevy.Communication.Melsec

MELSEC MC protocol client for Mitsubishi Q/L-compatible PLCs. It sends 3E and 4E frames over TCP and UDP, and 4C frames (format 4 with ASCII, format 5 with binary and an optional sum check) over a serial port. It provides typed reads and writes, a loopback heartbeat, and automatic reconnect from the channel family.

> **Preview and validation status**: the frame encoding follows the MELSEC communication protocol reference manual (SH-080008) and is checked against the manual's examples. It has **not been verified against a real PLC**. The items under [Not Yet Verified](#not-yet-verified-p2-d16) are inferred from the manual's figures and must be checked on hardware before production use.

## Target Frameworks

`net472` and `net8.0`.

## Installation

```text
dotnet add package Junevy.Communication.Melsec --prerelease
```

The package depends on `Junevy.Communication.Plc`, `Junevy.Communication.Tcp`, `Junevy.Communication.Udp` and `Junevy.Communication.Serial`.

## Scope

- **Frames**: 3E and 4E over TCP and UDP; 4C over serial, format 4 with `McEncoding.Ascii` and format 5 with `McEncoding.Binary`.
- **Commands**: batch read (0401) and batch write (1401) in word and bit units, and the loopback test (0619), which the heartbeat uses.
- **Devices**: Q/L-compatible device specification (subcommand 0000 for words, 0001 for bits). The device table is below.
- **Not supported**: random read and write (0403, 1402), multiple-block read and write (0406, 1406), remote RUN and STOP, CPU model read, iQ-R extended device specification, the 1E, 1C, 2C and 3C frames, 4C formats 1 to 3, and device name extensions (008x).

## Quick Start (TCP)

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
        if (!write.IsSuccess)
            throw new InvalidOperationException($"D100 could not be written: {write.ErrorKind}. {write.ErrorMessage}");

        CommResult<short> read = await plc.ReadInt16Async("D100", cancellationToken);
        if (!read.IsSuccess)
            throw new InvalidOperationException($"D100 could not be read: {read.ErrorKind}. {read.ErrorMessage}");
    }
}
```

The typed API is the same as the `Junevy.Communication.Plc` package: `ReadInt16Async`, `WriteFloatAsync`, `ReadStringAsync` and the other methods listed in its README.

## Transports

```csharp
using Junevy.Communication.Melsec;
using Junevy.Communication.Serial;
using Junevy.Communication.Udp;

public static class McTransportsExample
{
    public static McClient CreateUdp()
        => McClient.CreateUdp(
            new UdpChannelConfig { RemoteHost = "192.168.1.10", RemotePort = 5001 },
            new McClientOptions { FrameType = McFrameType.Frame4E, Encoding = McEncoding.Ascii });

    public static McClient CreateSerial()
        => McClient.CreateSerial(
            new SerialChannelConfig { PortName = "COM1", BaudRate = 9600 },
            new McClientOptions { FrameType = McFrameType.Frame4C, Encoding = McEncoding.Binary, SumCheck = true });
}
```

- **TCP and UDP** accept 3E and 4E. UDP is a datagram transport: a 4E reply is matched by its serial number, and a late reply is discarded.
- **Serial** accepts 4C only. `McEncoding.Ascii` selects format 4 and `McEncoding.Binary` selects format 5. Set `SumCheck` to match the PLC's configuration.
- The framing and correlation of the transport configuration must keep their defaults. The client sets them from the options, and throws `ArgumentException` when a transport configuration changes them.

## Options

`McClientOptions` derives from `PlcClientOptions`.

| Property | Default | Meaning |
|---|---|---|
| `FrameType` | `Frame3E` | `Frame3E` or `Frame4E` over TCP and UDP; `Frame4C` over serial |
| `Encoding` | `Binary` | `Binary` or `Ascii`. For serial it selects format 5 or format 4 |
| `NetworkNumber`, `PcNumber`, `ModuleIoNumber`, `ModuleStationNumber` | `0x00`, `0xFF`, `0x03FF`, `0x00` | Access route. The defaults address the local CPU module |
| `StationNumber`, `SelfStationNumber` | `0x00` | Station numbers |
| `MonitoringTimer` | `0x0004` | The PLC's monitoring timer in units of 250 ms, so `4` is 1 s. Keep it below the transport's request timeout (2000 ms by default) |
| `SumCheck` | `true` | Sum check on serial frames (format 4 and format 5) |
| `DataFormat` | `null` (word order `DCBA`, ASCII strings) | Word order and string encoding of typed values |
| `RetryCount` | `0` | Inherited. Retries after a retryable failure |
| `RetryInterval` | `1000` ms | Inherited. How long to wait for the link before a retry |
| `AllowReadModifyWriteBits` | `false` | Inherited. Allows a bit write into a word device by read-modify-write |

## Addresses

- Format `<device><number>[.<bit>]`, case-insensitive: `D100`, `d100`, `M0`, `X1A`, `ZR0`, `D100.F`.
- The number uses the device's radix (column "Radix" below). `X`, `Y`, `B`, `W`, `SB`, `SW`, `DX`, `DY` and `ZR` are hexadecimal, so `X1A` is number `0x1A`.
- The bit index after the dot is hexadecimal `0` to `F`. `D100.F` is bit 15 of `D100`.
- Reading one bit of a word device reads the word and selects the bit. Writing one returns `NotSupported` unless `AllowReadModifyWriteBits` is `true`. Then the client reads and writes the word under its operation lock and logs a warning on each write.
- A number above the limit of the frame returns `InvalidRequest`: `0xFFFFFF` for binary frames and for hexadecimal devices in ASCII frames, and `999999` for decimal devices in ASCII frames (six characters).

### Devices

| Device | Unit | Radix | ASCII code | Binary code |
|---|---|---|---|---|
| SM | Bit | 10 | `SM` | `0x91` |
| SD | Word | 10 | `SD` | `0xA9` |
| X | Bit | 16 | `X*` | `0x9C` |
| Y | Bit | 16 | `Y*` | `0x9D` |
| M | Bit | 10 | `M*` | `0x90` |
| L | Bit | 10 | `L*` | `0x92` |
| F | Bit | 10 | `F*` | `0x93` |
| V | Bit | 10 | `V*` | `0x94` |
| B | Bit | 16 | `B*` | `0xA0` |
| D | Word | 10 | `D*` | `0xA8` |
| W | Word | 16 | `W*` | `0xB4` |
| TS | Bit | 10 | `TS` | `0xC1` |
| TC | Bit | 10 | `TC` | `0xC0` |
| TN | Word | 10 | `TN` | `0xC2` |
| SS | Bit | 10 | `SS` | `0xC7` |
| SC | Bit | 10 | `SC` | `0xC6` |
| SN | Word | 10 | `SN` | `0xC8` |
| CS | Bit | 10 | `CS` | `0xC4` |
| CC | Bit | 10 | `CC` | `0xC3` |
| CN | Word | 10 | `CN` | `0xC5` |
| SB | Bit | 16 | `SB` | `0xA1` |
| SW | Word | 16 | `SW` | `0xB5` |
| S | Bit | 10 | `S*` | `0x98` |
| DX | Bit | 16 | `DX` | `0xA2` |
| DY | Bit | 16 | `DY` | `0xA3` |
| Z | Word | 10 | `Z*` | `0xCC` |
| R | Word | 10 | `R*` | `0xAF` |
| ZR | Word | 16 | `ZR` | `0xB0` |

`ZR` is a hexadecimal word device. Its number is `ZR0` to `ZRFFFFFF` in binary frames.

## Limits per Request

| Request | Points per request |
|---|---|
| Word read or write (words) | 960 |
| Bit read or write, Ethernet, binary | 7168 |
| Bit read or write, Ethernet, ASCII | 3584 |
| Bit read or write, serial | 7904 |

Larger operations are split into packets automatically. Each packet is retried on its own when `RetryCount` is greater than zero. For example, a read of 2000 words is sent as 960, 960 and 80 words.

## Heartbeat and Reconnect

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Melsec;
using Junevy.Communication.Tcp;

public static class McResilienceExample
{
    public static McClient Create()
    {
        var transport = new TcpClientChannelConfig
        {
            Host = "192.168.1.10",
            Port = 5000,
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 1000 },
            Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 2000, MaxFailures = 3 },
        };
        return McClient.CreateTcp(transport, new McClientOptions { FrameType = McFrameType.Frame3E });
    }
}
```

- The heartbeat sends the loopback test (0619) with the fixed data `1234ABCD`. The PLC must return the same bytes. A different reply is a `ProtocolViolation`.
- The heartbeat and the reconnect are off by default. Enable them in the transport configuration as shown above.

## Errors

- A PLC error response is `RemoteError`. `ProtocolErrorCode` carries the raw end code, and the message has the form `MC end code 0xC051.`. The package has no table that explains the codes (P2-D12): look the code up in the manual.
- The other `ErrorKind` values are described in the `Junevy.Communication.Plc` README.

## Dependency Injection

```csharp
using System;
using Junevy.Communication.Melsec.DependencyInjection;
using Junevy.Communication.Plc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class McDependencyInjectionExample
{
    public static void Register(IServiceCollection services)
        => services.AddMelsec();

    public static IPlcClient Create(IServiceProvider provider, IConfiguration configuration)
    {
        McClientConfig config = configuration.GetSection("Mc").Get<McClientConfig>()
            ?? throw new InvalidOperationException("The Mc section is missing.");
        return provider.GetRequiredService<PlcClientFactory>().GetOrAdd("line-1", config);
    }
}
```

`AddMelsec()` registers the MELSEC creator and the `PlcClientFactory` singleton. It can be called more than once. The configuration can come from `appsettings.json`:

```json
{
  "Mc": {
    "Transport": "Tcp",
    "Tcp": { "Host": "192.168.1.10", "Port": 5000 },
    "Options": { "FrameType": "Frame3E", "Encoding": "Binary" }
  }
}
```

`Transport` is `Tcp`, `Udp` or `Serial`. The matching configuration section (`Tcp`, `Udp` or `Serial`) is required, and creating the client without it throws `ArgumentException`. A serial client needs `"FrameType": "Frame4C"`.

## Not Yet Verified (P2-D16)

These two items are inferred from the manual's figures. They are covered by tests that use the manual's examples only:

1. **Format 4 response sum check**: the response sum check covers the bytes from the frame identifier through ETX. The manual's figure for formats 1 to 4 is read to include ETX; format 5's figure excludes DLE ETX.
2. **Format 5 error response**: the 2-byte error code is read as little-endian, like the other 2-byte values.

## Not in P2

Random read and write, multiple-block read and write, remote RUN and STOP, CPU model read, iQ-R extended device specification, the 1E, 1C, 2C and 3C frames, 4C formats 1 to 3, device name extensions, `PlcAddressUnit.Byte` (for S7), per-call data format overrides, and a public operation-lock API.
