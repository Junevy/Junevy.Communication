# Junevy.Communication

Asynchronous .NET communication libraries for industrial equipment and factory systems. The repository provides one package per protocol or transport, so an application references only what it uses.

- Targets .NET Framework 4.7.2 (`net472`) and .NET 8 (`net8.0`).
- Async APIs with explicit timeouts, cancellation, reconnect and retry options.
- Failures come back as results with a machine-readable error kind (`CommResult`, `WebApiResult<T>`), so the application decides what to do.
- Factories and Microsoft dependency-injection registration for managing several devices or endpoints by name.

## Protocols

| Protocol | Package | Status | What it covers | Documentation |
|---|---|---|---|---|
| Modbus RTU / TCP | `Junevy.Communication.Modbus` | 2.0.0 | Modbus master (client) over TCP and serial RTU: read and write coils and registers, raw requests, reconnect and retry, a connection factory | [Modbus README](Junevy.Communication.Modbus/README.md) |
| MELSEC MC | `Junevy.Communication.Melsec` | preview | Mitsubishi MC protocol: 3E and 4E frames (binary or ASCII) over TCP and UDP, 4C frames over serial; typed reads and writes by device address such as `D100` or `M0` | [Melsec README](Junevy.Communication.Melsec/README.md) |
| Siemens S7 | `Junevy.Communication.Siemens` | preview | S7comm over ISO-on-TCP (port 102) for S7-1200 and S7-1500: byte-addressed `DB`, `M`, `I` and `Q` areas, typed reads and writes by address such as `DB1.DBW12`, S7 `String` and `WString` fields, PDU-size handshake | [Siemens README](Junevy.Communication.Siemens/README.md) |
| Omron FINS | `Junevy.Communication.Omron` | preview | FINS over TCP (port 9600): `CIO`, `W`, `H`, `A`, `D` and `EM` areas, typed reads and writes by address such as `D100` or `W10.03`, node-address handshake, native bit writes | [Omron README](Junevy.Communication.Omron/README.md) |
| SECS-II / HSMS (SECS/GEM equipment) | `Junevy.Communication.Secs` | preview | Equipment side of a SECS-II link over HSMS-SS: SECS-II items and messages, Select and Linktest keepalive, passive or active session, handler dispatch with S9 system messages, transactions and reconnect | [Secs README](Junevy.Communication.Secs/README.md) |
| MES WebApi (REST, SOAP) | `Junevy.Communication.WebApi` | preview | Reporting to a MES over HTTP: a REST client with JSON and business-envelope support, and a SOAP 1.1 / 1.2 client; retries that respect idempotency, availability tracking, events and interceptors (signing, token refresh) | [WebApi README](Junevy.Communication.WebApi/README.md) |

MELSEC, S7 and FINS are built on the protocol-neutral PLC suite, which other address-based PLC protocols reuse ([Plc README](Junevy.Communication.Plc/README.md)). S7 and FINS are preview packages that have not been verified against real CPUs. SECS-II and HSMS-SS are not address-based: they use the channel family directly, cover the equipment side only, and have not been verified against real equipment or a real host. The MES WebApi package currently covers the client side; the HTTP server side, where the MES sends requests to the device, is planned.

## Transports and building blocks

These packages carry the protocols above and can also be used directly for custom byte protocols.

| Package | Status | What it covers | Documentation |
|---|---|---|---|
| `Junevy.Communication.Tcp` | preview | TCP client and server, optional TLS | [Tcp README](Junevy.Communication.Tcp/README.md) |
| `Junevy.Communication.Udp` | preview | UDP, directed and undirected | [Udp README](Junevy.Communication.Udp/README.md) |
| `Junevy.Communication.Serial` | preview | Serial port | [Serial README](Junevy.Communication.Serial/README.md) |
| `Junevy.Communication.Channels` | preview | Shared channel behaviour: framing, request/response correlation, heartbeat, reconnect | [Channels README](Junevy.Communication.Channels/README.md) |
| `Junevy.Communication.Core` | preview | Results and error kinds, backoff policies, timeouts, named registry | [Core README](Junevy.Communication.Core/README.md) |

Preview packages are versioned `1.0.0-preview.1`, and their public API may still change. Install them with `--prerelease`, for example:

```text
dotnet add package Junevy.Communication.Melsec --prerelease
```

## License

MIT
