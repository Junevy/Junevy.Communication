---
name: using-junevy-modbus
description: Use when a .NET project integrates the Junevy.Communication.Modbus library or repository (Modbus TCP/RTU master client) — writing connect/read/write code, DI or factory registration, handling ModbusResult or ModbusErrorKind, or troubleshooting reconnect/retry/timeout behavior
---

# Using Junevy.Communication.Modbus

## Overview

Modbus TCP/RTU **master (client)** library for .NET (net472 + net8.0) by Junevy. Core principles:

- **Result-based, not exception-based**: reads/writes return `ModbusResult<T>` (`IsSuccess`, `Data`, `ErrorMessage`, `ErrorKind`) instead of throwing. Parameter validation throws `ArgumentException`; `Request`/`RequestAsync` never throw on protocol violations. `ModbusResult<T>` is immutable and sealed; create results only through `ModbusResult<T>.Success` / `Fail`.
- **Config-object clients**: `new ModbusTcpClient(config)` / `new ModbusRtuClient(config)` — no `ConnectAsync(host, port)` overloads. `ConnectAsync(CancellationToken)` throws `OperationCanceledException` when cancelled and returns false when the connection fails or times out.
- **The library never mutates your request objects** and returns them untouched.

## When NOT to Use

Slave (server) simulation, Modbus ASCII, or gateways — not covered by this library.

## ⚠️ Renamed API Trap (pre-rename names do not exist)

| Wrong (old / guessed) | Correct (current) |
|---|---|
| `ModbusTCP` / `ModbusRTU` | `ModbusTcpClient` / `ModbusRtuClient` |
| `ModbusTCPConfig` / `ModbusRTUConfig` | `ModbusTcpClientConfig` / `ModbusRtuClientConfig` |
| `ReadTimeOut` / `WriteTimeOut` | `ReadTimeout` / `WriteTimeout` |
| `request.Start` / `request.Length` | `request.StartAddress` / `request.Quantity` |
| `SetPort(502)` | `Port` is a plain settable property (`config.Port = 1502;`) |
| `CheckConnection()` | Use `IsConnected` |
| `ConnectAsync(host, port)` | `Connect()` / `ConnectAsync()` use the config's `Address`/`Port` |
| NModbus-style `ushort[]` return | `ModbusResult<ushort[]>` — values in `.Data` |

## Quick Reference — Namespaces

| Namespace | Types |
|---|---|
| `...Modbus.Tcp` | `ModbusTcpClient`, `ModbusTcpClientConfig` |
| `...Modbus.Rtu` | `ModbusRtuClient`, `ModbusRtuClientConfig` |
| `...Modbus.Core.Models` | `ModbusRequest`, `ModbusResult<T>`, `ModbusErrorKind`, `ModbusFunctionCode`, `ModbusExceptionCode` |
| `...Modbus.Extensions` | `ModbusExtensions` (all Read/Write helpers) |
| `...Modbus.Factory` | `IModbusFactory`, `ModbusFactory` |
| `...Modbus.DependencyInjection` | `AddModbusFactory(this IServiceCollection)` |
| `...Modbus.Utils` | `ModbusHelper`, `Crc16Helper` |

(`...` = `Junevy.Communication`)

## Minimal Usage

### TCP

```csharp
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Tcp;

using var modbus = new ModbusTcpClient(new ModbusTcpClientConfig
{
    Address = "192.168.1.100",
    Port = 1502,               // plain property; default 502
    ReadTimeout = 2000,
    WriteTimeout = 2000
});

if (!modbus.Connect())
    return;                    // Connect returns bool, does not throw

var result = modbus.ReadHoldingRegisters(slaveId: 1, start: 100, length: 4);
Console.WriteLine(result.IsSuccess
    ? string.Join(", ", result.Data!)      // ushort[]
    : $"{result.ErrorMessage} [{result.ErrorKind}]");
modbus.Disconnect();
```

### RTU

```csharp
using Junevy.Communication.Modbus.Rtu;
using System.IO.Ports;

using var modbus = new ModbusRtuClient(new ModbusRtuClientConfig
{
    PortName = "COM3", BaudRate = 9600, Parity = Parity.None,
    DataBits = 8, StopBits = StopBits.One
});
modbus.Connect();
var coils = await modbus.ReadCoilsAsync(1, 0, 8);   // ModbusResult<bool[]>
```

### Factory + DI (multi-connection, recommended for services)

```csharp
services.AddModbusFactory();
var factory = provider.GetRequiredService<IModbusFactory>();

var plc = factory.GetOrAdd("plc-1", new ModbusTcpClientConfig
    { Address = "192.168.1.100", Reconnect = true, RetryCount = 3 });

// RS-485 multi-drop: one physical port, aliases per logical slave
factory.GetOrAdd("rs485-bus", new ModbusRtuClientConfig { PortName = "COM3" });
factory.RegisterAlias("slave-1", "rs485-bus");
```

Removing the master key (factory.TryRemove("rs485-bus")) also removes every alias that points to it and disposes the connection. RegisterAlias returns false when the target key does not exist.

Extension methods (all 15 function codes): `ReadCoils/DiscreteInputs/HoldingRegisters/InputRegisters(+Async)`, `WriteSingleCoil/Register`, `WriteMultipleCoils/Registers(+Async)`, `Diagnostics`, `GetCommEventCounter/Log`, `ReportServerId`, `MaskWriteRegister`, `ReadWriteMultipleRegisters`. Raw protocol: `modbus.Request(new ModbusRequest { SlaveId, FunctionCode, StartAddress, Quantity, Data })`.

## Behavioral Contracts

- **Transaction ID (TCP)**: auto-assigned per request by the client; matched exactly on response. Never set it yourself for transport calls.
- **Modbus exception responses** (slave returns FC|0x80) are **terminal failures**: `IsSuccess=false`, `ErrorKind=ModbusException`, code in the message (e.g. `Code=0x02`). Never retried.
- **Reconnect is lazy/per-request**: with `Reconnect=true` a dropped connection is physically re-established at the next request attempt (fresh TCP handshake / serial re-open), after failures marked `Timeout`/`ConnectionClosed`/`ProtocolViolation` (TCP only). No background watchdog. Note: `Disconnect()` followed by a request **silently reconnects** when `Reconnect=true` — set `Reconnect=false` to stay disconnected.
- **Retry**: RetryCount = retries after the first attempt; RetryInterval ms between attempts. A failure that destroys the connection (TCP Timeout/ConnectionClosed, send failure) is retried only when Reconnect=true; with Reconnect=false the request returns immediately with the real ErrorKind.
- **Thread-safety**: one request at a time per connection (`SemaphoreSlim`). Cancel `RequestAsync` via `CancellationToken` (net8.0: instant; net472: reads and connects are aborted by closing the socket, so cancellation also takes effect immediately).
- **Timeouts**: ReadTimeout is the total deadline for receiving one complete response frame (sync and async). WriteTimeout is the deadline for sending one request frame. A timeout returns ErrorKind.Timeout.
- **Dispose**: Dispose() aborts in-flight I/O and waits for the in-flight request to exit. Requests that were running or queued return ErrorKind.ConnectionClosed; calling Request/Connect after Dispose throws ObjectDisposedException; Disconnect after Dispose is a no-op.

## Common Mistakes

- Treating `ErrorKind` as optional — always branch on `IsSuccess` first; `ErrorKind` (`Timeout`, `ConnectionClosed`, `ProtocolViolation`, `ModbusException`, `Cancelled`, `InvalidRequest`, `Unspecified`) is for machine decisions like reconnect/alerting.
- Retrying `ModbusException` results yourself — the client already suppresses retries for them.
- Setting `request.ProtocolType` for transport calls — the client passes its own protocol; the property is only read by the bare frame API (`ModbusHelper.BuildRequestFrame`).
- Building requests for 0x08/0x17 with plain payload assumptions — `Data` has per-function-code layouts (documented on `ModbusRequest.Data`).
