# Junevy.Communication.Modbus

Modbus RTU/TCP master (client) communication library for .NET. It provides low-level request APIs, convenient extension methods for common function codes, and a factory for managing multiple Modbus connections.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

## Minimal Usage: Manual `new`

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

- `RetryCount` is the number of retries after the first attempt. For TCP, a timeout or a closed connection can only be retried when `Reconnect = true`; with `Reconnect = false` the request returns immediately with the real error (`Timeout` / `ConnectionClosed`).
- When `Reconnect = true`, a failed or closed TCP socket is recreated before the next retry. Note: this also applies after an explicit `Disconnect()` — the next request silently reconnects. Set `Reconnect = false` if you want `Disconnect()` to stay disconnected.
- For RTU, a faulted or closed serial port is closed and reopened before the next retry.
- Modbus exception responses are never retried (terminal failures).
- Communication failures are returned as `ModbusResult.Fail(...)` where possible instead of escaping as unhandled exceptions; parameter-validation errors throw standard exceptions (`ArgumentException` etc.).
