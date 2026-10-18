# Junevy.Communication.Modbus

Modbus master (client) for .NET, built on the Junevy.Communication address-based PLC suite. Version 3.0.0-preview.1.

- Frames: MBAP (Modbus TCP and Modbus UDP) and RTU (serial, RTU over TCP, RTU over UDP).
- Transports: TCP, UDP and serial.
- Targets: `net472` and `net8.0`.
- Async only. Every public operation returns a `CommResult` or `CommResult<T>`; no operation throws for communication or device failures.

Version 3 replaces the 2.x API. If you are upgrading from 2.x, read [Migrating from 2.x](#migrating-from-2x) first. The 2.x code is kept at the `modbus-v2.0.0` tag.

## Quick start

### Modbus TCP (MBAP)

```csharp
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;
using Junevy.Communication.Tcp;
using Junevy.Communication.Core.Results;

using ModbusClient client = ModbusClient.Create(
    new TcpClientChannelConfig { Host = "192.168.1.10", Port = 502 },
    new ModbusClientOptions { UnitId = 1 });

CommResult connected = await client.ConnectAsync();
CommResult<ushort[]> registers = await client.ReadUInt16Async("HR100", 2);
CommResult written = await client.WriteUInt16Async("HR100", 1234);
```

### RTU over a serial port

On a serial transport the default framing is RTU, so no framing option is needed.

```csharp
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Core.Results;

using ModbusClient client = ModbusClient.Create(
    new SerialChannelConfig { PortName = "COM3", BaudRate = 9600 },
    new ModbusClientOptions { UnitId = 1 });

await client.ConnectAsync();
CommResult<bool[]> coils = await client.ReadBitsAsync("C0", 8);
```

### RTU over TCP (a gateway or a serial server)

```csharp
using Junevy.Communication.Modbus;
using Junevy.Communication.Tcp;

using ModbusClient client = ModbusClient.Create(
    new TcpClientChannelConfig { Host = "192.168.1.20", Port = 502 },
    new ModbusClientOptions { Framing = ModbusFraming.Rtu, UnitId = 3 });
```

### One connection, several stations

Put the station in the address prefix. One client can poll a whole RS-485 bus.

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus;
using Junevy.Communication.Tcp;
using ModbusClient client = ModbusClient.Create(
    new TcpClientChannelConfig { Host = "192.168.1.10", Port = 502 },
    new ModbusClientOptions());
await client.ConnectAsync();
CommResult<ushort[]> first = await client.ReadUInt16Async("s=1;HR0", 1);
CommResult<ushort[]> second = await client.ReadUInt16Async("s=2;HR0", 1);
```

## Addresses

An address is `[s=<station>;]<area><number>[.<bit>]`, case-insensitive.

| Form | Example | Meaning |
|---|---|---|
| Symbolic (zero-based) | `HR100` | Holding register 100 (protocol address 100) |
| Symbolic | `C5`, `DI7`, `IR3` | Coil, discrete input, input register |
| Modicon (one-based) | `40101` | Holding register 100 (the first digit selects the area) |
| Modicon, six digits | `400101` | Holding register 100 |
| Bit of a register | `HR100.5` | Bit 5 of register 100 (bit 0 is the least significant) |
| Station prefix | `s=2;HR100` | Station 2 for this request |

- Area digits in the Modicon form: `0` coil, `1` discrete input, `3` input register, `4` holding register. Five digits give numbers 0 to 9998 (`40001` to `49999`); six digits give numbers 0 to 65535 (`400001` to `465536`).
- Numbers are decimal and must be 0 to 65535. The address must not run past 65535.
- A bit index is allowed only on registers (`IR`, `HR`) and only from 0 to 15. Coils and discrete inputs are bit devices and have no bit index.
- The station prefix is 0 to 255. On RTU it must be 1 to 247 (`0` is broadcast, which is not supported; 248 to 255 are reserved).
- Without a prefix the request uses `ModbusClientOptions.UnitId`.

## Function codes and single-request limits

| Operation | Address area | Function code |
|---|---|---|
| Read words | `HR` / `IR` | 03 / 04 |
| Read bits | `C` / `DI` | 01 / 02 |
| Write words | `HR` | 06 for one word (when `UseSingleWriteFunctions` is true), otherwise 10 |
| Write bits | `C` | 05 for one point (when `UseSingleWriteFunctions` is true), otherwise 0F |
| Write a bit of a register | `HR` | 16 (mask write) when `UseMaskWriteForBits` is true; otherwise read-modify-write when `AllowReadModifyWriteBits` is true; otherwise `NotSupported` |
| Write to `IR` or `DI` | — | Rejected with `InvalidRequest`; nothing is sent |

The single-write rule applies to each packet. A 124-word write sends 123 words with 10, then one word with 06.

Operations split into packets automatically:

| Limit | Value |
|---|---|
| Read registers per packet | 125 |
| Read bits per packet | 2000 |
| Write registers per packet | 123 |
| Write coils per packet | 1968 |
| Mask write | one bit per request |

Reads of coils and discrete inputs by bit, and register writes by word, all split at these limits. `MaskWriteRegisterAsync` and `ReadWriteMultipleRegistersAsync` never split.

## Typed access

The typed extensions from the suite apply: `ReadInt16Async`, `ReadUInt16Async`, `ReadInt32Async`, `ReadUInt32Async`, `ReadFloatAsync`, `ReadDoubleAsync`, `ReadBoolAsync`, `ReadStringAsync`, and the matching `Write…Async` methods.

Multi-register values are ordered by `ModbusClientOptions.DataFormat`. The default is `ABCD` (high word first) with ASCII strings. Use `new PlcDataFormat(WordOrder.CDAB)` for low-word-first devices.

```csharp
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Modbus;
using Junevy.Communication.Plc;

var options = new ModbusClientOptions { DataFormat = new PlcDataFormat(WordOrder.CDAB) };
```

## Options

`ModbusClientOptions` adds the Modbus options to the suite options (`RetryCount`, `RetryInterval`, `AllowReadModifyWriteBits`).

| Option | Default | Meaning |
|---|---|---|
| `Framing` | `null` | `ModbusFraming.Mbap` or `ModbusFraming.Rtu`. `null` means RTU on serial and MBAP on network transports. MBAP on serial is rejected. |
| `UnitId` | `1` | Station used when the address has no prefix. |
| `UseSingleWriteFunctions` | `true` | Use 05 and 06 for single points and words. Set to `false` for devices that accept only 0F and 10. |
| `UseMaskWriteForBits` | `false` | Write register bits with 0x16 (atomic on the device). |
| `DataFormat` | ABCD, ASCII | Word order and string encoding. |
| `RetryCount` | `0` | Retries for `Timeout`, `ConnectionClosed` and `ProtocolViolation`. |
| `RetryInterval` | `1000` | Milliseconds to wait for the connection before a retry. |
| `AllowReadModifyWriteBits` | `false` | Allow a register bit write by read-modify-write when mask write is off. |

Timeouts and connection behaviour belong to the channel configuration: `RequestTimeout`, `ConnectTimeout`, `SendTimeout`, `Heartbeat`, `Reconnect`, `ResetOnRequestTimeout`.

## Mask write and read-write multiple

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus;
using Junevy.Communication.Tcp;
using ModbusClient client = ModbusClient.Create(
    new TcpClientChannelConfig { Host = "192.168.1.10", Port = 502 },
    new ModbusClientOptions());
await client.ConnectAsync();
// 0x16: result = (current AND andMask) OR (orMask AND NOT andMask). One request, atomic on the device.
CommResult masked = await client.MaskWriteRegisterAsync("HR100", andMask: 0xFFDF, orMask: 0x0020);

// 0x17: the device writes first, then reads. Both addresses must be holding registers on the same station.
CommResult<byte[]> readBack = await client.ReadWriteMultipleRegistersAsync(
    readAddress: "HR3", readCount: 6, writeAddress: "HR14", writeData: new byte[] { 0x00, 0xFF, 0x00, 0xFF });
```

`ReadWriteMultipleRegistersAsync` takes 1 to 125 registers to read and 1 to 121 registers (2 to 242 bytes) to write. It returns the bytes read, two per register, in big-endian order.

## Error handling

Every operation returns a result. Check `IsSuccess` and read `ErrorKind`.

| Kind | Meaning | Retried |
|---|---|---|
| `RemoteError` | The device returned an exception response. `ProtocolErrorCode` is the exception code (01 to 0B). | No |
| `ProtocolViolation` | A response had the wrong station, function, byte count, echo or CRC. | Yes |
| `Timeout` | No response within `RequestTimeout`. A late response is discarded. | Yes |
| `ConnectionClosed` | The connection closed during the request. | Yes |
| `NotConnected` | The client is disposed or not connected. Nothing is sent. | No |
| `InvalidRequest` | Bad address, count or argument. Nothing is sent. | No |
| `NotSupported` | The combination is not supported (for example a bit write without a mask-write or read-modify-write option). | No |
| `Cancelled` | The caller cancelled. | No |
| `Unspecified` | An unexpected failure. The error is logged. | No |

The message of an exception response names the standard codes:

```text
Modbus exception 0x02 (Illegal Data Address) for function 0x03.
```

The nine standard names are 01 Illegal Function, 02 Illegal Data Address, 03 Illegal Data Value, 04 Server Device Failure, 05 Acknowledge, 06 Server Device Busy, 08 Memory Parity Error, 0A Gateway Path Unavailable and 0B Gateway Target Device Failed to Respond. Other codes have no name.

## Heartbeat and reconnect

The heartbeat reads one holding register (`HR0`, one word) from the default station. A correctly formatted response is healthy, and so is an exception response, because a device that answers with an exception still has a working link. Only a wrong station, function or format is unhealthy.

The heartbeat and the background reconnect are off by default. Enable them in the channel configuration:

```csharp
using Junevy.Communication.Channels;
var config = new TcpClientChannelConfig
{
    Host = "192.168.1.10",
    Port = 502,
    Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 2000, MaxFailures = 3 },
    Reconnect = new ReconnectOptions { Enabled = true },
};
```

On a byte-stream transport, a request timeout resets the connection by default (`ResetOnRequestTimeout`). With RTU over TCP the connection is rebuilt in the background, so wait for it with `WaitForConnectedAsync` before the next request.

## Dependency injection

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
    Options = new ModbusClientOptions { UnitId = 1 },
});
```

`ModbusClientConfig` binds from configuration. Its `Transport` selects one of `Tcp`, `Udp` or `Serial`, and that configuration must be present. `AddModbus()` and `AddMelsec()` can be used together in one factory.

## Not provided

- Modbus ASCII.
- Server (slave) mode.
- Diagnostic functions (07, 08, 0B, 0C, 11), file records (14, 15), FIFO (18) and device identification (2B).
- Broadcast (station 0).
- Custom function codes and a raw PDU entry point.
- A synchronous API.

## Migrating from 2.x

Version 3 keeps the wire behaviour and the same function codes, but it changes the API and some defaults.

| 2.x | 3.0.0-preview.1 | Note |
|---|---|---|
| `new ModbusTcpClient(new ModbusTcpClientConfig { Address, Port })` | `ModbusClient.Create(new TcpClientChannelConfig { Host, Port }, new ModbusClientOptions())` | Creation does not connect. Use `ConnectAsync()`. |
| `new ModbusRtuClient(new ModbusRtuClientConfig { PortName, BaudRate, … })` | `ModbusClient.Create(new SerialChannelConfig { PortName, BaudRate, … }, new ModbusClientOptions())` | `FrameReadInterval` is gone. RTU frames are delimited by their function code. |
| RTU over a TCP gateway (not supported in 2.x) | `ModbusClientOptions { Framing = ModbusFraming.Rtu }` on a TCP config | New in 3.0. |
| `Connect()` / `ConnectAsync()` returning `bool` | `ConnectAsync()` returns `CommResult` | There is no synchronous API. |
| `Disconnect()` | `DisconnectAsync()` | After disconnect, requests return `NotConnected` and do not reconnect silently. |
| `IsConnected` | `IsConnected`, `State`, `StateChanged` | |
| `Reconnect = true` (lazy reconnect on the next request) | `Reconnect` in the channel configuration (background reconnect) | Off by default. |
| `RetryCount` (default 3), `RetryInterval` | `RetryCount` (default 0), `RetryInterval` | Retries only `Timeout`, `ConnectionClosed` and `ProtocolViolation`. |
| `ConnectTimeout`, `ReadTimeout`, `WriteTimeout` | `ConnectTimeout` / `SendTimeout` / `RequestTimeout` on the channel configuration | |
| `ReadHoldingRegistersAsync(slave, start, n)` returning `ModbusResult<ushort[]>` | `ReadUInt16Async($"s={slave};HR{start}", n)` returning `CommResult<ushort[]>` | `ReadAsync` returns raw bytes. |
| `ReadInputRegistersAsync` | `ReadUInt16Async("IR…", n)` | |
| `ReadCoilsAsync`, `ReadDiscreteInputsAsync` | `ReadBitsAsync("C…", n)`, `ReadBitsAsync("DI…", n)` | |
| `WriteSingleRegisterAsync(slave, a, v)` | `WriteUInt16Async("HR…", v)` | Uses 06 by default. |
| `WriteMultipleRegistersAsync(slave, a, values)` | `WriteUInt16Async("HR…", values)` or `WriteAsync("HR…", bytes)` | Packets split above 123 words. |
| `WriteSingleCoilAsync`, `WriteMultipleCoilsAsync` | `WriteBoolAsync("C…", v)`, `WriteBitsAsync("C…", values)` | |
| `MaskWriteRegisterAsync(slave, a, and, or)` | `MaskWriteRegisterAsync("HR…", and, or)` | Bits of registers can also use `UseMaskWriteForBits`. |
| `ReadWriteMultipleRegistersAsync(…)` | `ReadWriteMultipleRegistersAsync(readAddress, readCount, writeAddress, writeData)` | Data is bytes. |
| `ReadExceptionStatus`, `Diagnostics`, `GetCommEventCounter`, `GetCommEventLog`, `ReportServerId` | Not provided | Diagnostic functions are out of scope. |
| `Request(ModbusRequest)`, `RequestAsync(ModbusRequest)`, `ModbusHelper.BuildRequestFrame` | Not provided | There is no raw PDU entry point. |
| `ModbusResult<T>`, `ModbusErrorKind` | `CommResult<T>`, `CommErrorKind` | Equal numbers: `ModbusException` (6) is `RemoteError` (6). The exception code is in `ProtocolErrorCode`. |
| `ModbusException` (thrown) | Not thrown; argument errors return `InvalidRequest` | |
| `IModbusFactory`, `ModbusConnectionManager`, `ModbusFactoryBuilder`, `AddModbusFactory()` | `PlcClientFactory`, `PlcClientFactoryBuilder`, `AddModbus()` | |
| RS-485 multi-slave with one client per slave and `slaveId` argument | One client and a station prefix (`s=n;`) | |
| `IModbusPduValidator`, `IModbusFrameBuilder` injection | Not provided | Validation is internal to the protocol. |

## Verification status

- The library has been tested against simulated devices in the test suite, not against real hardware. It has not been checked with Modbus Poll, Modbus Slave or any other third-party tool.
- Resynchronisation of RTU frames on a noisy line has not been measured.
- Mask write (0x16) is not supported by every device. Where it is not, the device returns exception 01.
- Some devices accept only 0F and 10 and reject 05 and 06. Set `UseSingleWriteFunctions = false` for them.
- Station 0 and station 255 mean different things on TCP gateways depending on the vendor.
- RTU over UDP is rare. It is implemented from the specification only.
