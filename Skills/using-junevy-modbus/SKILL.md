---
name: using-junevy-modbus
description: Use when a .NET project integrates the Junevy.Communication.Modbus library (Modbus TCP, RTU and UDP master) and was written against the 2.x API (ModbusTcpClient, ModbusRtuClient, ModbusResult, IModbusFactory, AddModbusFactory) or needs the v2 to v3 migration — or when a 3.0 project must choose between the old and the new names. For writing new 3.0 code, the protocol details are in using-junevy-plc.
---

# Using Junevy.Communication.Modbus (v2 → v3 migration)

## Overview

Version 3.0.0-preview.1 rebuilds the Modbus master on the Junevy.Communication PLC suite. The 2.x API is gone. There are no compatibility shims and no `[Obsolete]` forwarders. The 2.x code is kept at the local tag `modbus-v2.0.0`.

Use this skill for the migration. For code you write now, read `using-junevy-plc` (its "Modbus Quick Start" section and the address and framing rules).

## When NOT to Use

- Code that is already on 3.0 and only needs a request: use `using-junevy-plc`.
- Modbus ASCII, server (slave) mode, diagnostic functions, file records, FIFO, device identification, broadcast, custom PDUs or a synchronous API: not provided in 3.0.

## Quick Reference — Namespaces

| 2.x | 3.0 |
|---|---|
| `Junevy.Communication.Modbus.Tcp.ModbusTcpClient` | `Junevy.Communication.Modbus.ModbusClient` |
| `Junevy.Communication.Modbus.Rtu.ModbusRtuClient` | `Junevy.Communication.Modbus.ModbusClient` |
| `Junevy.Communication.Modbus.Core.Models.ModbusResult<T>` | `Junevy.Communication.Core.Results.CommResult<T>` |
| `Junevy.Communication.Modbus.Core.Models.ModbusErrorKind` | `Junevy.Communication.Core.Results.CommErrorKind` |
| `Junevy.Communication.Modbus.Factory.IModbusFactory` | `Junevy.Communication.Plc.PlcClientFactory` |
| `Junevy.Communication.Modbus.DependencyInjection` (`AddModbusFactory`) | `Junevy.Communication.Modbus.DependencyInjection` (`AddModbus`) |

## Migration Map

| 2.x | 3.0 | Note |
|---|---|---|
| `new ModbusTcpClient(new ModbusTcpClientConfig { Address, Port })` | `ModbusClient.Create(new TcpClientChannelConfig { Host, Port }, new ModbusClientOptions())` | Creation does not connect. |
| `new ModbusRtuClient(new ModbusRtuClientConfig { PortName, BaudRate, … })` | `ModbusClient.Create(new SerialChannelConfig { PortName, BaudRate, … }, new ModbusClientOptions())` | RTU is the default on serial. |
| `Connect()` / `ConnectAsync()` → `bool` | `ConnectAsync()` → `CommResult` | No synchronous API. |
| `Disconnect()` | `DisconnectAsync()` | Later requests return `NotConnected`; there is no silent reconnect. |
| `RetryCount` (default 3) | `RetryCount` (default 0) | Retries only `Timeout`, `ConnectionClosed` and `ProtocolViolation`. |
| `Reconnect = true` (lazy, on the next request) | `Reconnect` in the channel configuration (background) | Off by default. |
| `ReadHoldingRegistersAsync(slave, start, n)` → `ModbusResult<ushort[]>` | `ReadUInt16Async($"s={slave};HR{start}", n)` → `CommResult<ushort[]>` | Station is a prefix on the address. |
| `ReadInputRegistersAsync` | `ReadUInt16Async("IR…", n)` | |
| `ReadCoilsAsync` / `ReadDiscreteInputsAsync` | `ReadBitsAsync("C…", n)` / `ReadBitsAsync("DI…", n)` | |
| `WriteSingleRegisterAsync` | `WriteUInt16Async("HR…", v)` | 06 by default. |
| `WriteMultipleRegistersAsync` | `WriteUInt16Async("HR…", values)` | Splits above 123 words. |
| `WriteSingleCoilAsync` / `WriteMultipleCoilsAsync` | `WriteBoolAsync("C…", v)` / `WriteBitsAsync("C…", values)` | |
| `MaskWriteRegisterAsync(slave, a, and, or)` | `MaskWriteRegisterAsync("HR…", and, or)` | |
| `ReadWriteMultipleRegistersAsync(…)` | `ReadWriteMultipleRegistersAsync(readAddress, readCount, writeAddress, writeData)` | Data is bytes. |
| `ReadExceptionStatus`, `Diagnostics`, `GetCommEventCounter`, `GetCommEventLog`, `ReportServerId` | Not provided | |
| `Request(ModbusRequest)`, `RequestAsync(ModbusRequest)`, `ModbusHelper.BuildRequestFrame` | Not provided | No raw PDU entry point. |
| `ModbusResult<T>` / `ModbusErrorKind` | `CommResult<T>` / `CommErrorKind` | Numeric values match: `ModbusException` (6) is `RemoteError` (6); the exception code is in `ProtocolErrorCode`. |
| `ModbusException` thrown | Not thrown | Argument errors return `InvalidRequest`. |
| `IModbusFactory`, `ModbusConnectionManager`, `ModbusFactoryBuilder`, `AddModbusFactory()` | `PlcClientFactory`, `PlcClientFactoryBuilder`, `AddModbus()` | |
| RS-485 multi-slave with one client per slave and a `slaveId` argument | One client; station prefix `s=n;` | |
| `IModbusPduValidator`, `IModbusFrameBuilder` injection | Not provided | Validation is internal. |

## Factory and DI

```csharp
using Junevy.Communication.Modbus;
using Junevy.Communication.Modbus.DependencyInjection;
using Junevy.Communication.Plc;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new ServiceCollection();
services.AddModbus();
ServiceProvider provider = services.BuildServiceProvider();

PlcClientFactory factory = provider.GetRequiredService<PlcClientFactory>();
IPlcClient client = factory.GetOrAdd("line-1", new ModbusClientConfig
{
    Transport = ModbusTransport.Tcp,
    Tcp = new TcpClientChannelConfig { Host = "192.168.1.10", Port = 502 },
});
```

`factory.GetOrAdd` returns the existing client for a name and ignores the new configuration. Use a new name to create a client with different settings.

## Behavioural Contracts That Changed

- Results instead of exceptions: every operation returns a result. Branch on `IsSuccess`, then on `ErrorKind`.
- Retries are off by default and retry only the three transient kinds. An exception response (`RemoteError`) is never retried.
- A late response is discarded by its transaction ID (MBAP). It does not rebuild the TCP connection.
- A request timeout on RTU over TCP rebuilds the connection. Wait for it with `WaitForConnectedAsync` before the next request.
- `IR` and `DI` are read-only. A write to them returns `InvalidRequest` and sends nothing.

## Common Mistakes When Migrating

- Calling `ConnectAsync()` and expecting a `bool`. It returns `CommResult`; check `IsSuccess`.
- Keeping `RetryCount = 3` from 2.x. The 3.0 default is 0.
- Passing `slaveId` as an argument. Put the station in the address: `s=2;HR100`.
- Reading `ModbusResult.Data` without checking `IsSuccess`. `Data` is the default value on failure.
- Expecting `Modbus` to appear in the type names. The 3.0 client is `ModbusClient`, and the typed API is the suite's `PlcClientExtensions`.
- Creating MBAP on serial. It is rejected at creation.

For the full address, framing, limit and error reference, read `using-junevy-plc` and the Modbus README.
