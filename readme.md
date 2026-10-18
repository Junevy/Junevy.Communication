# Junevy.Communication

Asynchronous .NET communication libraries for industrial equipment and factory systems. The repository provides one package per protocol or transport, so an application references only what it uses.

- Targets .NET Framework 4.7.2 (`net472`) and .NET 8 (`net8.0`). The SDK adapter packages — `Junevy.Communication.Mqtt`, `Junevy.Communication.OpcUa`, and the FTP adapter planned for the same phase — target **.NET 8 only**, because the SDKs they adapt no longer support .NET Framework.
- Async APIs with explicit timeouts, cancellation, reconnect and retry options.
- Failures come back as results with a machine-readable error kind (`CommResult`, `WebApiResult<T>`), so the application decides what to do.
- Factories and Microsoft dependency-injection registration for managing several devices or endpoints by name.

## Protocols

| Protocol | Package | Status | What it covers | Documentation |
|---|---|---|---|---|
| Modbus TCP / RTU / UDP | `Junevy.Communication.Modbus` | 3.0.0-preview.1 | Modbus master (client) on the PLC suite: MBAP and RTU frames over TCP, UDP and serial; read and write coils, registers and register bits; mask write (0x16) and read-write multiple (0x17); station prefix `s=n;`; see the package README for the v2 to v3 migration |
| MELSEC MC | `Junevy.Communication.Melsec` | preview | Mitsubishi MC protocol: 3E and 4E frames (binary or ASCII) over TCP and UDP, 4C frames over serial; typed reads and writes by device address such as `D100` or `M0` | [Melsec README](Junevy.Communication.Melsec/README.md) |
| Siemens S7 | `Junevy.Communication.Siemens` | preview | S7comm over ISO-on-TCP (port 102) for S7-1200 and S7-1500: byte-addressed `DB`, `M`, `I` and `Q` areas, typed reads and writes by address such as `DB1.DBW12`, S7 `String` and `WString` fields, PDU-size handshake | [Siemens README](Junevy.Communication.Siemens/README.md) |
| Omron FINS | `Junevy.Communication.Omron` | preview | FINS over TCP (port 9600): `CIO`, `W`, `H`, `A`, `D` and `EM` areas, typed reads and writes by address such as `D100` or `W10.03`, node-address handshake, native bit writes | [Omron README](Junevy.Communication.Omron/README.md) |
| SECS-II / HSMS (SECS/GEM equipment) | `Junevy.Communication.Secs` | preview | Equipment side of a SECS-II link over HSMS-SS: SECS-II items and messages, Select and Linktest keepalive, passive or active session, handler dispatch with S9 system messages, transactions and reconnect | [Secs README](Junevy.Communication.Secs/README.md) |
| MES WebApi (REST, SOAP) | `Junevy.Communication.WebApi` | preview | Reporting to a MES over HTTP: a REST client with JSON and business-envelope support, and a SOAP 1.1 / 1.2 client; retries that respect idempotency, availability tracking, events and interceptors (signing, token refresh) | [WebApi README](Junevy.Communication.WebApi/README.md) |
| MES WebApi server (HTTP) | `Junevy.Communication.WebApi.Server` | preview | HTTP server for MES on embedded Kestrel (.NET 8 only): routes and handlers, JSON envelope or Problem Details, authentication, interceptors, events, idempotent replay | [WebApi.Server README](Junevy.Communication.WebApi.Server/README.md) |
| MQTT | `Junevy.Communication.Mqtt` | preview | MQTT 3.1.1 and 5.0 client (client only, .NET 8 only): connect, publish, subscribe and receive, TLS, MQTT 5 properties, last will and testament, reconnect that restores the subscriptions | [Mqtt README](Junevy.Communication.Mqtt/README.md) |
| OPC UA | `Junevy.Communication.OpcUa` | preview | OPC UA client (client only, .NET 8 only): connect over the `None` security policy with anonymous login, read, write, browse and subscribe, per-item status codes, session reconnect | [OpcUa README](Junevy.Communication.OpcUa/README.md) |
| FTP / FTPS | `Junevy.Communication.Ftp` | preview | FTP and FTPS client (client only, passive mode, binary transfers, .NET 8 only): list, upload and download, atomic downloads, file and directory operations, explicit and implicit TLS, GBK file names | [Ftp README](Junevy.Communication.Ftp/README.md) |

MELSEC, S7 and FINS are built on the protocol-neutral PLC suite, which other address-based PLC protocols reuse ([Plc README](Junevy.Communication.Plc/README.md)). S7 and FINS are preview packages that have not been verified against real CPUs. SECS-II and HSMS-SS are not address-based: they use the channel family directly, cover the equipment side only, and have not been verified against real equipment or a real host. The MES WebApi package covers the client side (REST and SOAP); the HTTP server side, where the MES sends requests to the device, is the separate `Junevy.Communication.WebApi.Server` package (.NET 8 only, embedded Kestrel). The MQTT package is a thin adapter over [MQTTnet](https://github.com/dotnet/MQTTnet) and currently targets .NET 8 only; it has been verified against an in-process broker, not against a real one such as Mosquitto or EMQX. The OPC UA package is a thin adapter over the [OPC Foundation .NET Standard Stack](https://github.com/OPCFoundation/UA-.NETStandard), targets .NET 8 only, and currently supports **only** the `None` security policy with anonymous login — a server that disables `None` gives `NotSupported`. It has been verified against the official server hosted in-process, not against a real one such as KEPServerEX or Prosys. The FTP package is a thin adapter over [FluentFTP](https://github.com/robinrodricks/FluentFTP) 55, targets .NET 8 only, and is not SFTP. It has been verified against an in-process fake FTP server, not against a real one such as vsftpd or FileZilla Server.

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
