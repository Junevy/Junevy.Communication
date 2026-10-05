# Junevy.Communication.Modbus

Modbus RTU/TCP master (client) communication library for .NET. It provides low-level request APIs, convenient extension methods for common function codes, and a factory for managing multiple Modbus connections.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

Project path:

```text
Junevy.Communication.Modbus/Junevy.Communication.Modbus.csproj
```

## Supported Function Codes

| Code | Name | Extension method |
|---|---|---|
| `0x01` | Read Coils | `ReadCoils` / `ReadCoilsAsync` |
| `0x02` | Read Discrete Inputs | `ReadDiscreteInputs` / `ReadDiscreteInputsAsync` |
| `0x03` | Read Holding Registers | `ReadHoldingRegisters` / `ReadHoldingRegistersAsync` |
| `0x04` | Read Input Registers | `ReadInputRegisters` / `ReadInputRegistersAsync` |
| `0x05` | Write Single Coil | `WriteSingleCoil` / `WriteSingleCoilAsync` |
| `0x06` | Write Single Register | `WriteSingleRegister` / `WriteSingleRegisterAsync` |
| `0x07` | Read Exception Status | `ReadExceptionStatus` / `ReadExceptionStatusAsync` |
| `0x08` | Diagnostics | `Diagnostics` / `DiagnosticsAsync` |
| `0x0B` | Get Comm Event Counter | `GetCommEventCounter` / `GetCommEventCounterAsync` |
| `0x0C` | Get Comm Event Log | `GetCommEventLog` / `GetCommEventLogAsync` |
| `0x0F` | Write Multiple Coils | `WriteMultipleCoils` / `WriteMultipleCoilsAsync` |
| `0x10` | Write Multiple Registers | `WriteMultipleRegisters` / `WriteMultipleRegistersAsync` |
| `0x11` | Report Server ID | `ReportServerId` / `ReportServerIdAsync` |
| `0x16` | Mask Write Register | `MaskWriteRegister` / `MaskWriteRegisterAsync` |
| `0x17` | Read/Write Multiple Registers | `ReadWriteMultipleRegisters` / `ReadWriteMultipleRegistersAsync` |

## Minimal Usage: Manual `new`

### Modbus TCP

```csharp
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Tcp;

using var modbus = new ModbusTcpClient(new ModbusTcpClientConfig
{
    Address = "192.168.1.100",
    Port = 502,
    ConnectTimeout = 2000,
    ReadTimeout = 2000,
    WriteTimeout = 2000
});

if (!modbus.Connect())
{
    Console.WriteLine("Connect failed.");
    return;
}

var result = modbus.ReadHoldingRegisters(slaveId: 1, start: 0, length: 4);
Console.WriteLine(result.IsSuccess
    ? string.Join(", ", result.Data!)
    : result.ErrorMessage);

modbus.Disconnect();
```

### Modbus RTU

```csharp
using System.IO.Ports;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Rtu;

using var modbus = new ModbusRtuClient(new ModbusRtuClientConfig
{
    PortName = "COM3",
    BaudRate = 9600,
    Parity = Parity.None,
    DataBits = 8,
    StopBits = StopBits.One,
    ReadTimeout = 2000,
    WriteTimeout = 2000
});

modbus.Connect();

var write = modbus.WriteSingleRegister(slaveId: 1, start: 100, value: 1234);
Console.WriteLine(write.IsSuccess ? "Write OK" : write.ErrorMessage);

modbus.Disconnect();
```

## Recommended Usage: Factory + DI

```csharp
using Junevy.Communication.Modbus.DependencyInjection;
using Junevy.Communication.Modbus.Extensions;
using Junevy.Communication.Modbus.Factory;
using Junevy.Communication.Modbus.Tcp;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddLogging();
services.AddModbusFactory();

using var provider = services.BuildServiceProvider();
var factory = provider.GetRequiredService<IModbusFactory>();

var plc = factory.GetOrAdd("plc-1", new ModbusTcpClientConfig
{
    Address = "192.168.1.100",
    Port = 502,
    Reconnect = true,
    RetryCount = 3,
    RetryInterval = 200
});

plc.Connect();

var coils = await plc.ReadCoilsAsync(slaveId: 1, start: 0, length: 8);
Console.WriteLine(coils.IsSuccess
    ? string.Join(", ", coils.Data!)
    : coils.ErrorMessage);

plc.Disconnect();
```

For RS-485 multi-drop RTU scenarios, register one physical RTU connection and add aliases for logical slave names:

```csharp
factory.GetOrAdd("rs485-bus", new ModbusRtuClientConfig { PortName = "COM3" });
factory.RegisterAlias("slave-1", "rs485-bus");
factory.RegisterAlias("slave-2", "rs485-bus");
```

## Manual Construction Without Microsoft DI (Prism, etc.)

For host containers that do not use `IServiceCollection` (Prism, DryIoc, Unity, ...), build the factory with `ModbusFactoryBuilder` and register the instance directly. Unset options mirror the `AddModbusFactory` defaults; no Microsoft DI package is required:

```csharp
using Junevy.Communication.Modbus.Factory;
using Microsoft.Extensions.Logging;

// e.g. inside Prism's RegisterTypes(IContainerRegistry containerRegistry):
var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger)); // or NullLoggerFactory.Instance
var factory = ModbusFactoryBuilder.Create()
    .WithLoggerFactory(loggerFactory)      // optional: used by the factory and every client it creates
    .WithConnectionManager(manager)        // optional: share the registry with the host container
    .Build();

containerRegistry.RegisterInstance<IModbusFactory>(factory);
```

`Build()` returns a new, independent factory per call. Dispose the factory on application shutdown (it owns the connections).

## Raw Request API

When a device uses custom behavior, call `Request` / `RequestAsync` directly:

```csharp
using Junevy.Communication.Modbus.Core.Models;

var raw = plc.Request(new ModbusRequest
{
    SlaveId = 1,
    FunctionCode = ModbusFunctionCode.Diagnostics,
    Data = new byte[] { 0x00, 0x00, 0x12, 0x34 }
});
```

`ModbusRequest` is a pure data class. `Data` has a per-function-code layout (documented on the property); the transport never mutates a request you pass in.

## Transaction ID (TCP)

You do not need to manage `TransactionId` for TCP requests. The client assigns an auto-incrementing transaction id per logical request (wrapping at `ushort.MaxValue`) before sending, and matches responses by exact transaction id — a late response from a previous request can never be paired with a new one. Manual assignment only affects the bare frame-building API (`ModbusHelper.BuildRequestFrame`).

## Error Classification

Every failed `ModbusResult` carries a machine-readable `ErrorKind` (`ModbusErrorKind`) in addition to `ErrorMessage`:

| Kind | Meaning |
|---|---|
| `Unspecified` | Default for legacy/uncategorized failures |
| `InvalidRequest` | The request failed local validation before being sent |
| `ConnectionClosed` | The connection was down, dropped, or could not be (re)established |
| `Timeout` | Connect/read/write timeout |
| `ProtocolViolation` | Malformed frame, invalid length, or CRC failure |
| `ModbusException` | The slave answered with a Modbus exception response (the exception code is in the message, e.g. `Code=0x02`) |
| `Cancelled` | The `CancellationToken` fired before completion |

Modbus exception responses are terminal: they are returned immediately as failed results and are never retried.

## Reconnect and Retry

Both TCP and RTU transports support request-level retry and optional reconnect:

```csharp
var tcp = new ModbusTcpClient(new ModbusTcpClientConfig
{
    Address = "192.168.1.100",
    Port = 502,
    Reconnect = true,
    RetryCount = 3,
    RetryInterval = 200,
    ConnectTimeout = 2000,
    ReadTimeout = 2000,
    WriteTimeout = 2000
});

var rtu = new ModbusRtuClient(new ModbusRtuClientConfig
{
    PortName = "COM3",
    Reconnect = true,
    RetryCount = 3,
    RetryInterval = 200
});
```

Behavior:

- `RetryCount` is the number of retries after the first attempt.
- When `Reconnect = true`, a failed or closed TCP socket is recreated before the next retry. Note: this also applies after an explicit `Disconnect()` — the next request silently reconnects. Set `Reconnect = false` if you want `Disconnect()` to stay disconnected.
- For RTU, a faulted or closed serial port is closed and reopened before the next retry.
- Modbus exception responses are never retried (terminal failures).
- Communication failures are returned as `ModbusResult.Fail(...)` where possible instead of escaping as unhandled exceptions; parameter-validation errors throw standard exceptions (`ArgumentException` etc.).

## Notes

- One request at a time is serialized per connection with `SemaphoreSlim`.
- RTU frames use CRC16 verification.
- TCP responses validate MBAP protocol id, transaction id, and unit id.
- Register addresses are protocol-level zero-based addresses.
- For industrial field use, keep polling intervals larger than the slave response time, set explicit timeouts, enable reconnect for long-running services, and log failed requests with enough device context to diagnose wiring/network faults.
