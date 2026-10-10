# Junevy.Communication.Serial

Serial port channel (`SerialChannel`) on top of `Junevy.Communication.Channels`. It wraps `SerialPort.BaseStream` (not the `DataReceived` event), so framing, request/response correlation, heartbeat and reconnect work exactly as they do for TCP. Reconnect closes and re-opens the port.

> Preview (`1.0.0-preview.1`): the public API may change before the P2 milestone (PLC protocol suite and MELSEC validation). See the root `readme.md`.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

## Installation

```text
dotnet add package Junevy.Communication.Serial --prerelease
```

## Delimited ASCII Device

```csharp
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Serial;

var config = new SerialChannelConfig
{
    PortName = "COM3",
    BaudRate = 115200,
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
    OpenTimeout = 2000,
    RequestTimeout = 1000,
};

await using var scanner = new SerialChannel(config);
CommResult opened = await scanner.ConnectAsync();
if (!opened.IsSuccess)
{
    Console.WriteLine(opened.ErrorMessage);
    return;
}

// The delimiter is appended automatically on send and removed from the reply.
CommResult<byte[]> reply = await scanner.RequestAsync(Encoding.ASCII.GetBytes("MEASURE"));
Console.WriteLine(reply.IsSuccess ? Encoding.ASCII.GetString(reply.Data!) : $"{reply.ErrorKind}: {reply.ErrorMessage}");
```

## Binary Device With Idle-Gap Framing and Reconnect

The default framing is `IdleGap` with a 20 ms gap. A frame ends after the port has been silent for `GapTimeout`. USB-serial adapters often add latency, so the gap must be longer than the adapter's latency timer (16 ms on FTDI devices by default). The 30 ms value here leaves a margin.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Serial;

var config = new SerialChannelConfig
{
    PortName = "COM7",
    BaudRate = 19200,
    Framing = new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = 30 },
    RequestTimeout = 1000,
    Reconnect = new ReconnectOptions
    {
        Enabled = true,
        OnInitialFailure = true,               // keep trying if the adapter is not plugged in yet
        Interval = 2000,
        MaxInterval = 10000,
    },
};

await using var port = new SerialChannel(config);
port.StateChanged += (sender, e) => Console.WriteLine($"{e.PreviousState} -> {e.CurrentState} ({e.Reason})");

await port.ConnectAsync();                     // a failed result is not final when OnInitialFailure is set
CommResult<byte[]> reply = await port.RequestAsync(new byte[] { 0x02, 0x01, 0x00, 0x03 });
Console.WriteLine(reply.IsSuccess ? BitConverter.ToString(reply.Data!) : $"{reply.ErrorKind}: {reply.ErrorMessage}");
```

## Heartbeat

A serial write almost always succeeds, even when the device is silent. The built-in heartbeat therefore requires `ExpectedReply`, or a `ChannelComponents.HealthProbe`, and the constructor throws `ArgumentException` otherwise.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Serial;

var config = new SerialChannelConfig
{
    PortName = "COM3",
    BaudRate = 9600,
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
    Heartbeat = new HeartbeatOptions
    {
        Enabled = true,
        Interval = 5000,
        Timeout = 1000,
        MaxFailures = 3,
        Payload = "PING",                      // the delimiter is appended on send
        ExpectedReply = "PONG",                // compared with the delivered reply, which has no delimiter
    },
    Reconnect = new ReconnectOptions { Enabled = true },
};

await using var device = new SerialChannel(config);
await device.ConnectAsync();
```

## RS-485: One Port, Several Slaves

Open each physical port with one `SerialChannel`. Two instances on the same port cannot both open it. Give the logical slaves aliases of the shared channel, so each one sends through the same queue and the protocol layer picks the reply by slave address.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Serial;

using ChannelFactory factory = ChannelFactoryBuilder.Create()
    .WithCreator(new SerialChannelCreator())
    .Build();

factory.GetOrAdd("rs485-bus", new SerialChannelConfig
{
    PortName = "COM5",
    BaudRate = 9600,
    Framing = new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = 30 },
});
factory.RegisterAlias("slave-1", "rs485-bus");
factory.RegisterAlias("slave-2", "rs485-bus");

if (factory.TryGet("slave-1", out IClientChannel? slave1))
{
    CommResult<byte[]> reply = await slave1.RequestAsync(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x02 });
    Console.WriteLine(reply.IsSuccess ? "slave 1 answered" : reply.ErrorMessage);
}
```

Removing the bus (`factory.TryRemove("rs485-bus")`) also removes both aliases and closes the port.

## Timeouts

| Setting | Default | Meaning |
|---|---|---|
| `OpenTimeout` | 2000 ms | Total time to open the port, including retries after "access denied" (every 20 ms). A timeout returns `Timeout`; the port is released when the driver returns |
| `HandshakeTimeout` | 5000 ms | `IConnectionInitializer`; the port is closed if it does not finish in time |
| `SendTimeout` | 2000 ms | Writing one frame; a timeout closes the port, and the channel re-opens it only if reconnect is enabled |
| `RequestTimeout` | 2000 ms | Per request; the port is never re-opened because of a request timeout |
| `LateReplyWindow` | −1 (= `RequestTimeout`) | Replies arriving after a timeout are dropped for this long |
| `IdleTimeout` | 0 (off) | No input for this long closes the port (re-opened only if reconnect is enabled) |
| `PartialFrameTimeout` | 0 (off) | Residual bytes of an incomplete frame are discarded after this long; the port stays open |
| `DisconnectTimeout` | 1000 ms | Graceful close: frames already received are delivered within this time |
| `Framing.GapTimeout` (`IdleGap`) | 20 ms | Silence that ends a frame; must exceed the adapter's latency timer |
| `Heartbeat.Interval` / `Timeout` / `MaxFailures` | 5000 / 2000 / 3 | Liveness probe; `ExpectedReply` is required |
| `Reconnect.Interval` / `MaxInterval` | 1000 / 30000 ms | Backoff between attempts to re-open the port |

`ReadBufferSize` and `WriteBufferSize` set the driver's buffers (must be positive and even, as `SerialPort` requires). They do not change how many bytes the channel reads at a time.

## Platform Notes

- The same `SerialPort` API is used on `net472` and `net8.0`.
- On `net472`, `SerialPort.BaseStream` ignores cancellation tokens. A write that exceeds `SendTimeout` or a cancelled write is handled by closing the port, which fails the operation the same way.
- **USB-serial hot-plug is not verified.** The design identifies a risk: on .NET Framework, unplugging an adapter while its port is open might raise an unhandled exception from `SerialPort` internals and end the process. This has not been reproduced or ruled out. Do not rely on hot-plug in production until it has been tested on the target machine.
- A re-plugged adapter can get a different COM number. The channel keeps the name it was configured with and does not look for the device.

## More

For the complete behavioural contract, including the open-retry rule, the late-reply window and the hot-plug checklist, see `Skills/using-junevy-channels/SKILL.md` in the repository.
