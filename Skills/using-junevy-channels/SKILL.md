---
name: using-junevy-channels
description: Use when a .NET project references Junevy.Communication.Tcp, Junevy.Communication.Udp, Junevy.Communication.Serial or Junevy.Communication.Channels, or needs TCP, UDP or serial communication, a TCP server, TLS, framing, heartbeats or automatic reconnect with the Junevy channel family — writing client, server, serial or UDP code, DI or ChannelFactory registration, handling ConnectionState, NotConnected or CommErrorKind results, or troubleshooting disconnects, late replies and timeouts
---

# Using Junevy Channels (TCP / UDP / Serial)

## Overview

Byte-channel library family for .NET (net472 + net8.0) by Junevy, version `1.0.0-preview.1`. The public API may change before P2 (the PLC protocol suite and MELSEC validation). Core principles:

- **Result-based**: `ConnectAsync`, `SendAsync`, `RequestAsync` and `ReceiveAsync` return `CommResult` / `CommResult<byte[]>`. Branch on `IsSuccess`, then on `ErrorKind`. Never match `ErrorMessage` text.
- **Exceptions are for programming errors and cancellation**: the caller's cancellation token throws `OperationCanceledException` (`ConnectAsync` included). Invalid configuration throws `ArgumentException` (or a subclass) at construction. Use after `Dispose` throws `ObjectDisposedException`.
- **Background lifecycle**: the channel detects a lost link itself (receive loop, heartbeat, idle timeout). With `Reconnect.Enabled`, it reconnects in the background. Unlike Modbus, nothing waits for the next request.
- **Configuration is read once**: construction validates the configuration and takes a snapshot. Changing the configuration object afterwards has no effect. `Config` returns the same object you passed in, so do not expect it to be a copy.
- **Frames are copies**: `FrameReceivedEventArgs.Data` is a fresh `byte[]` you may keep.

## When NOT to Use

- Modbus TCP or RTU. Use `using-junevy-modbus`, which has its own result types and reconnect rules.
- PLC address-based access (typed reads and writes), protocol suites, SECS/GEM, OPC UA, MQTT, FTP, or WebApi. None of these are in P1.
- Modbus ASCII, DTLS (TLS over UDP), or a guarantee that a USB-serial hot-plug is safe (see the checklist below).

## Package Selection

| Need | Package | Client type | Creator for `ChannelFactory` | DI extension (namespace) |
|---|---|---|---|---|
| TCP client | `Junevy.Communication.Tcp` | `TcpClientChannel` (`ITcpClientChannel`) | `TcpClientChannelCreator` | `AddTcpChannels()` (`Junevy.Communication.Tcp.DependencyInjection`) |
| TCP server | `Junevy.Communication.Tcp` | `TcpServer` (`ITcpServer`), sessions `ITcpSession` | none: the host creates and disposes it | none |
| UDP | `Junevy.Communication.Udp` | `UdpChannel` (`IUdpChannel`) | `UdpChannelCreator` | `AddUdpChannels()` (`Junevy.Communication.Udp.DependencyInjection`) |
| Serial port | `Junevy.Communication.Serial` | `SerialChannel` (`ISerialChannel`) | `SerialChannelCreator` | `AddSerialChannels()` (`Junevy.Communication.Serial.DependencyInjection`) |
| Named factory | `Junevy.Communication.Channels` | `ChannelFactory` | n/a | `AddChannels()` (`Junevy.Communication.Channels.DependencyInjection`) |
| Custom stream transport | `Junevy.Communication.Channels` | subclass of `StreamClientChannel` | your own `IChannelCreator` | none |

- `AddTcpChannels()`, `AddUdpChannels()` and `AddSerialChannels()` call `AddChannels()` and register their creator. Repeated calls are idempotent. The container then resolves `ChannelFactory` as a singleton.
- Without a DI container (Prism and similar hosts), build the factory with `ChannelFactoryBuilder`, register the instance in the host, and dispose it at shutdown. Each `Build()` returns a new, independent factory.
- The factory manages client channels only. `TcpServer` is not registered in it (see Server).
- Creators are selected by the **exact** runtime type of the configuration. Subclasses do not match a base-type creator. No creator for that type throws `NotSupportedException`. Two creators for one type throw `ArgumentException` when the factory is built.
- `GetOrAdd(name, config, components)` returns the existing channel when the name is already registered, and ignores its config. `TryAdd` returns false in that case. `TryRemove` disposes the channel and removes its aliases. `RegisterAlias(alias, existing)` makes two names share one instance. `RemoveAlias` removes only the alias.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.DependencyInjection;
using Junevy.Communication.Serial;
using Junevy.Communication.Serial.DependencyInjection;
using Junevy.Communication.Tcp;
using Junevy.Communication.Tcp.DependencyInjection;
using Junevy.Communication.Udp.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddTcpChannels();        // also calls AddChannels()
services.AddUdpChannels();
services.AddSerialChannels();

using ServiceProvider provider = services.BuildServiceProvider();
ChannelFactory factory = provider.GetRequiredService<ChannelFactory>();

IClientChannel plc = factory.GetOrAdd("plc-1", new TcpClientChannelConfig { Host = "192.168.1.100", Port = 502 });
IClientChannel scanner = factory.GetOrAdd("scanner", new SerialChannelConfig { PortName = "COM4", BaudRate = 9600 });
factory.RegisterAlias("scanner-main", "scanner");
```

## Lifecycle and States

`ConnectionState`: `Disconnected`, `Connecting`, `Connected`, `Reconnecting`, `Disconnecting`, `Disposed`.

| From | Event | To |
|---|---|---|
| Disconnected | `ConnectAsync` | Connecting, then Connected on success |
| Connecting | connect fails | Disconnected; or Reconnecting when `Reconnect.Enabled` and `Reconnect.OnInitialFailure` |
| Connected | link lost (remote closed, read error, send failed, heartbeat failed, idle timeout, protocol violation, TCP request timeout, serial port removed) | Reconnecting when `Reconnect.Enabled`; otherwise Disconnected |
| Reconnecting | reconnect succeeds | Connected |
| Reconnecting | `MaxAttempts` reached (`ReconnectExhausted`) or `DisconnectAsync` | Disconnected |
| Connected | `DisconnectAsync` | Disconnecting, then Disconnected (`UserRequested`) |
| any | `Dispose` | Disposed |

- `DisconnectReason` (on `StateChanged` and in `Reason`): `None`, `UserRequested`, `RemoteClosed`, `SendFailed`, `RequestTimeout`, `HeartbeatFailed`, `IdleTimeout`, `PartialFrameTimeout`, `ProtocolViolation`, `AuthenticationFailed`, `ReconnectExhausted`, `Error`, `Disposed`.
- `ConnectAsync` while already Connected returns success at once. While Reconnecting it cancels the background loop and tries once immediately.
- A failed `ConnectAsync` returns the failure; it does not throw. The `ErrorKind` is `Timeout`, `ConnectionClosed`, `AuthenticationFailed` or `InvalidRequest` (TLS certificate could not be loaded).
- `DisconnectAsync` is graceful. It stops background reconnection, fails in-flight requests with `ConnectionClosed`, and delivers frames already received within `DisconnectTimeout`. Nothing reconnects afterwards: call `ConnectAsync` again. After `Dispose` it is a no-op.
- `Dispose()` is immediate. It does not drain received frames, fails in-flight requests with `ConnectionClosed`, and is idempotent. The synchronous form waits at most `DisconnectTimeout + 1000` ms, then returns while the release finishes in the background. After `Dispose`, every member except `DisconnectAsync` throws `ObjectDisposedException`. Prefer `await using`.
- Use `DisconnectAsync` first when you want frames already received to be delivered, and `Dispose` only when shutting down.
- `WaitForConnectedAsync(timeout)`: `timeout` is in milliseconds; `0` only checks the current state, `-1` waits indefinitely. Returns false on timeout or disposal, and throws `OperationCanceledException` when the caller cancels.
- `SendAsync`, `RequestAsync` and `ReceiveAsync` return `NotConnected` at once while not Connected. Nothing is queued, and nothing connects implicitly.
- `Statistics` (`ConnectionStatistics`): `BytesSent`, `BytesReceived`, `FramesSent`, `FramesReceived`, `FramesDropped`, `ProtocolErrors`, `ReconnectCount`, `ConsecutiveHeartbeatFailures`, `LastSentAt`, `LastReceivedAt`, `ConnectedSince`.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var config = new TcpClientChannelConfig
{
    Host = "192.168.1.100",
    Port = 502,
    Reconnect = new ReconnectOptions { Enabled = true, OnInitialFailure = true, Interval = 1000, MaxInterval = 30000 },
};

await using var device = new TcpClientChannel(config);
device.StateChanged += (sender, e) => Console.WriteLine($"{e.PreviousState} -> {e.CurrentState}: {e.Reason} (attempt {e.ReconnectAttempt})");

CommResult connected = await device.ConnectAsync();
bool ready = await device.WaitForConnectedAsync(timeout: 5000);
Console.WriteLine($"first attempt: {connected.ErrorKind}; ready within 5 s: {ready}");

await device.DisconnectAsync();                    // graceful: no automatic reconnection from here on
```

## Threads and Events

- `StateChanged`, `FrameReceived` and the server events are raised on thread-pool threads, one at a time, in order. WPF code must marshal to the `Dispatcher`.
- Handlers may call `DisconnectAsync`, `Dispose`, `StopAsync` or `CloseAsync` without deadlocking. Those calls return in bounded time. A `Dispose` inside a handler does not wait for that handler to return.
- An exception in one handler is logged and does not stop the other handlers or the receive loop.
- Replies to `RequestAsync` do not wait behind `FrameReceived`. They are matched before frames enter the event queue, so a slow handler delays only unsolicited frames.
- Dispatch queue (`ReceiveQueueCapacity`, default 1024). TCP uses `QueueFullMode.Wait`, which applies backpressure to the peer. Serial and UDP default to `DropOldest`, which counts drops in `FramesDropped` and logs a warning every 100 drops. A `TcpServer` session always uses `Wait`.

## Logging

A channel logs only when it is given a logger; by default it logs nothing. Connection events are logged at Information and above. Every frame is logged in hex at `Debug`, as `TX` and `RX` lines, with category names such as `Junevy.Communication.Tcp.TcpClientChannel`. To see the frames, set `Junevy.Communication` to `Debug` in the host's configuration (`"Logging": { "LogLevel": { "Junevy.Communication": "Debug" } }`), or pass a logger that is enabled at `Debug`:

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;

// Requires the Microsoft.Extensions.Logging.Console package.
using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Debug)
    .AddConsole());

// Direct construction: pass an ILogger<T> as the second argument.
await using var device = new TcpClientChannel(
    new TcpClientChannelConfig { Host = "192.168.1.100", Port = 5000 },
    loggerFactory.CreateLogger<TcpClientChannel>());

// Factory: the factory creates an ILogger<T> for each channel it builds.
using ChannelFactory factory = ChannelFactoryBuilder.Create()
    .WithCreator(new TcpClientChannelCreator())
    .WithLoggerFactory(loggerFactory)
    .Build();
```

With dependency injection, register logging (`AddLogging()` or the host's own setup) before `AddChannels()`; the registered `ChannelFactory` takes the container's `ILoggerFactory`.

## Framing

| `FramingMode` | Key parameters | Use |
|---|---|---|
| `Raw` (TCP default) | `MaxFrameLength` | Each read is delivered as a chunk; packet capture. Not for serial |
| `Delimiter` | `Delimiters`, `KeepDelimiter` (false), `AppendDelimiterOnSend` (true) | ASCII commands, barcode, vision |
| `FixedLength` | `FrameLength` | Fixed records |
| `LengthField` | `LengthFieldOffset`, `LengthFieldSize`, `LengthFieldEncoding`, `LengthAdjustment`, `InitialBytesToStrip` | Modbus TCP, S7, MELSEC, HSMS, custom binary |
| `StartEnd` | `StartMarker`, `EndMarker`, `KeepMarkers` (true) | STX…ETX; junk bytes before a start marker are skipped |
| `IdleGap` (serial default) | `GapTimeout` (20 ms) | Silence ends the frame; for devices without delimiter or length |

- `MaxFrameLength` (default 65536). For `Delimiter` it measures the bytes before the delimiter, so with `KeepDelimiter` a delivered frame can be `MaxFrameLength` plus the delimiter length. Other modes measure the whole frame on the wire. Exceeding it throws `FrameDecodeException` internally, which becomes `ProtocolViolation`: TCP disconnects, serial discards the buffer.
- `Delimiter`: consecutive delimiters produce no empty frame. `Delimiters[0]` is appended to every sent payload, so do not add it yourself. Delimiters are text (escapes `\r \n \t \0 \\ \xHH`, otherwise UTF-8) or `hex:` followed by hex digits, optionally spaced or dash-separated (for example `hex:0D0A`).
- `LengthField`: total frame length = `LengthFieldOffset + LengthFieldSize + length value + LengthAdjustment`. Encodings: `BinaryBigEndian`, `BinaryLittleEndian`, `AsciiHex`, `AsciiDecimal`. Sizes are 1, 2 or 4 for binary and 1–8 for ASCII. Defaults: `LengthFieldOffset` 0, `LengthFieldSize` 2, `LengthFieldEncoding` `BinaryBigEndian`, `LengthAdjustment` 0, `InitialBytesToStrip` 0. The delivered frame starts after `InitialBytesToStrip` bytes.
- Sending: only `Delimiter` changes the bytes (`Delimiters[0]` is appended unless `AppendDelimiterOnSend` is false). Every other mode sends the payload exactly as given, so a length header, a start or end marker, or padding must already be in the payload.
- `IdleGap`: the USB-serial adapter's latency timer delays bytes. FTDI devices default to 16 ms, so the 20 ms default leaves only 4 ms of margin. Raise `GapTimeout` (30–50 ms is typical) or lower the adapter's latency timer. A gap that is too large merges consecutive frames.
- Partial frames: `PartialFrameTimeout` disconnects TCP and discards residual bytes on serial (`PartialFrameAction.Discard`). It is 0 (off) by default.
- Keep `Raw` out of serial configurations. One read can return half a frame.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

// Modbus TCP style: 6-byte MBAP header, length in bytes 4-5 counts the bytes after it.
var modbusTcp = new FramingOptions
{
    Mode = FramingMode.LengthField,
    LengthFieldOffset = 4,
    LengthFieldSize = 2,
    LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
    MaxFrameLength = 260,
};

// ASCII frames delimited by CR LF, with the delimiter removed from the delivered frame.
var asciiLines = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" }, MaxFrameLength = 4096 };

// STX ... ETX with the markers kept in the frame.
var stxEtx = new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "hex:02", EndMarker = "hex:03", KeepMarkers = true };

IFrameCodecFactory codec = FrameCodecFactory.Create(modbusTcp);   // validates and copies the options
```

## Text Protocols

`ByteChannelTextExtensions` (namespace `Junevy.Communication.Channels`) adds `SendTextAsync`, `RequestTextAsync` and `ReceiveTextAsync` to every `IByteChannel`: client channels, `ITcpSession` and `IUdpChannel`.

- The text is encoded as UTF-8 without a BOM unless you pass an `Encoding` (for example `Encoding.ASCII`).
- Nothing is appended to the text. The framing encoder appends `Delimiters[0]` once (`AppendDelimiterOnSend`, on by default). Pair text calls with a `Delimiter` framing, and do not add a terminator yourself.
- Replies are decoded with the same encoding and do not include the delimiter (`KeepDelimiter` is false by default).
- A failed result keeps its `ErrorKind`, message, protocol code and exception. Decoding never turns a failure into a success.
- A `null` channel or text throws `ArgumentNullException`.

```csharp
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

await using var scanner = new TcpClientChannel(new TcpClientChannelConfig
{
    Host = "192.168.1.120",
    Port = 9004,
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
});

CommResult connected = await scanner.ConnectAsync();
if (!connected.IsSuccess)
{
    Console.WriteLine($"connect failed: {connected.ErrorKind} {connected.ErrorMessage}");
    return;
}

// UTF-8 without a BOM. The CR LF delimiter is appended once by the framing encoder; do not add it to the text.
CommResult sent = await scanner.SendTextAsync("TRIGGER");

// The reply is decoded with the same encoding. A failure keeps its ErrorKind.
CommResult<string> reply = await scanner.RequestTextAsync("READ?", new RequestOptions { Timeout = 1000 });
Console.WriteLine(reply.IsSuccess ? reply.Data : $"{reply.ErrorKind}: {reply.ErrorMessage}");

// Wait for an unsolicited line that the device writes in ASCII.
CommResult<string> line = await scanner.ReceiveTextAsync(new RequestOptions { Timeout = 5000 }, Encoding.ASCII);
```

## Request–Response Correlation

`CorrelationMode` (set with `Correlation` in the configuration, or `ChannelComponents.Correlation`):

- `Sequential` (default): one request at a time. The next inbound frame after the write is the reply. Queueing for the request lock does not count toward `RequestTimeout`, but the caller's token still applies.
- `Matcher`: several requests can be in flight. Each inbound frame is offered FIFO to the in-flight `IResponseMatcher` (`RequestOptions.Matcher`). Use it for devices that mix unsolicited reports with replies.
- `Keyed`: `ChannelComponents.KeyExtractor` (an `IFrameKeyExtractor`) is required. The constructor throws `ArgumentException` without it. `RequestAsync` ignores `RequestOptions.Matcher` and matches by key. `ReceiveAsync` uses the matcher in every mode. If a request key cannot be extracted, or the same key is already in flight, the request fails at once with `InvalidRequest`. `TryGetRequestKey` gets the bytes passed to `RequestAsync` (no appended delimiter). `TryGetResponseKey` gets the decoded frame, with `InitialBytesToStrip` bytes removed and no delimiter unless `KeepDelimiter` is true; offsets are relative to those bytes.
- Unclaimed frames go to `ReceiveAsync` waiters (FIFO), then to `FrameReceived`.
- A matcher's `IsMatch` receives an **empty request span** when the frame is for `ReceiveAsync`. Check the request length before indexing it. A matcher that throws is logged at Error and treated as not matching.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var config = new TcpClientChannelConfig
{
    Host = "192.168.1.100",
    Port = 5000,
    Correlation = CorrelationMode.Matcher,
    Framing = new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 1, LengthFieldSize = 1, LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian, LengthAdjustment = 2 },
};

await using var device = new TcpClientChannel(config);
await device.ConnectAsync();

Task<CommResult<byte[]>> status = device.RequestAsync(new byte[] { 0x10, 0x00 }, new RequestOptions { Matcher = new CommandMatcher() });
Task<CommResult<byte[]>> alarm = device.ReceiveAsync(new RequestOptions { Timeout = 10000, Matcher = new CommandMatcher() });
CommResult<byte[]> statusReply = await status;

// Matches a reply to the request that starts with the same command byte.
sealed class CommandMatcher : IResponseMatcher
{
    public bool IsMatch(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame)
        => request.Length > 0 && frame.Length > 0 && request[0] == frame[0];   // request is empty for ReceiveAsync
}
```

Timeouts and late replies:

- `RequestTimeout` (default 2000 ms) runs from the moment the frame is written until the matching reply arrives. `RequestOptions.Timeout` overrides it for one call.
- TCP `Sequential` with the default `ResetOnRequestTimeout = true`: a request timeout drops the connection, and it is re-established if reconnect is enabled. A **caller cancellation while waiting for the reply** is treated the same way.
- Otherwise a timeout is followed by the late-reply window (`LateReplyWindow`, default −1 = `RequestTimeout`). Serial and UDP always use it. TCP uses it when `ResetOnRequestTimeout = false`.
- Late-reply window, `Sequential`: frames that arrive inside it are dropped and counted in `FramesDropped`, and the request lock stays held, so the next request cannot mistake one for its own reply.
- Late-reply window, `Keyed`: up to 256 recently timed-out keys are remembered for the window. A reply with a remembered key is logged as a warning and dropped.
- Late-reply window, `Matcher`: a late reply cannot be identified, so it is delivered as an unclaimed frame.
- Cancelling a request that is still waiting for the request lock fails with `Cancelled` and sends nothing.
- Cancelling while the frame is being written counts as a send failure: the connection drops because a partial frame may be on the wire.
- UDP `RequestRetryCount` resends the same datagram after each timeout. Each attempt is timed separately, and a reply to any attempt completes the request.
- Frames that arrive during `IConnectionInitializer` (before the connection reports Connected) are held in a backlog of up to 64. A `ReceiveAsync` registered during the handshake sees them. After the handshake they are dispatched in order. More than 64 is a protocol violation.

## Heartbeat and Idle Timeout

- The built-in probe sends `Heartbeat.Payload` every `Interval`. With `ExpectedReply` set, the reply must equal the delivered frame byte for byte (no delimiter unless `KeepDelimiter`). Without it, a successful send counts as healthy. That is only meaningful for TCP.
- `Enabled` without `ChannelComponents.HealthProbe` or `HealthProbeFactory` requires `Payload`, otherwise the constructor throws `ArgumentException`.
- UDP and serial require `ExpectedReply`, or a `HealthProbe` or `HealthProbeFactory`. A send alone cannot show that the peer is alive. The constructor throws otherwise.
- UDP in undirected mode requires `HealthProbe` or `HealthProbeFactory`, because the built-in probe needs a remote endpoint.
- `Interval` is measured from the start of one probe to the start of the next. `MaxFailures` consecutive failures raise `HeartbeatFailed`. A silent peer is therefore detected after about `(MaxFailures − 1) × Interval + Timeout`. With the defaults (5000 ms, 2000 ms, 3) that is about 12 s.
- `OnlyWhenIdle` (default true) skips a probe when frames were exchanged since the last one.
- A probe's own request timeout does not drop the TCP connection. The probe counts that as a failure instead, and the normal `ResetOnRequestTimeout` rule does not apply.
- A probe that could not be sent because the channel was busy with another request is not counted as a failure. A probe that returns `NotConnected` is not counted either.
- A probe that needs the channel itself (for example, one that calls `RequestAsync`) comes from `ChannelComponents.HealthProbeFactory`. Client channels call it once, at the first successful open, and reuse the result across reconnects. Server sessions call it once per session. It replaces the `Payload` requirement. See Extension Points.
- `IdleTimeout` (milliseconds, 0 = off) disconnects when no **inbound** frame arrives for that long. Outbound traffic does not count.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;

var config = new TcpClientChannelConfig
{
    Host = "192.168.1.100",
    Port = 5000,
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
    IdleTimeout = 30000,                     // no inbound frame for 30 s: disconnect and reconnect
    Heartbeat = new HeartbeatOptions
    {
        Enabled = true,
        Interval = 5000,
        Timeout = 2000,
        MaxFailures = 3,
        Payload = "PING",                    // the delimiter is appended on send
        ExpectedReply = "PONG",              // compared with the delivered frame, which has no delimiter
    },
    Reconnect = new ReconnectOptions { Enabled = true },
};

await using var device = new TcpClientChannel(config);
await device.ConnectAsync();
```

## TCP Client

- Main settings and defaults: `Host` ("127.0.0.1"), `Port`, `LocalAddress`, `LocalPort` (0 = any), `ConnectTimeout` (2000 ms per attempt), `HandshakeTimeout` (5000 ms: TLS plus initializer), `SendTimeout` (2000), `RequestTimeout` (2000), `IdleTimeout` (0), `PartialFrameTimeout` (0), `DisconnectTimeout` (1000), `Framing` (Raw), `Correlation` (Sequential), `ResetOnRequestTimeout` (true), `LateReplyWindow` (−1), `Heartbeat`, `Reconnect`, `Socket`, `Tls`, `ReceiveQueueCapacity` (1024), `QueueFullMode` (Wait).
- A connect timeout returns `Timeout`. A refused connection, unreachable host or DNS failure returns `ConnectionClosed`, and the message includes the `SocketError`. IPv4 addresses are tried first.
- `RemoteEndPoint`, `LocalEndPoint` and `IsTlsActive` are valid only while connected.
- Socket options (`TcpSocketOptions`): `NoDelay` (true), `ReceiveBufferSize` and `SendBufferSize` (0 = system), `LingerTime` (−1 = system), `KeepAlive` (enabled; time 30000 ms, interval 5000 ms, 3 retries; on net472 the retry count is ignored).

## TCP Server

```csharp
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var server = new TcpServer(new TcpServerConfig
{
    ListenAddress = "0.0.0.0",
    Port = 5000,
    MaxSessions = 16,                        // 0 = unlimited
    AllowedRemoteAddresses = new[] { "192.168.1.10" },
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
});

server.SessionConnected += (sender, e) => Console.WriteLine($"session {e.Session.Id} connected from {e.Session.RemoteEndPoint}");
server.SessionClosed += (sender, e) => Console.WriteLine($"session {e.Session.Id} closed: {e.Reason}");
server.FrameReceived += async (sender, e) => await e.Session.SendAsync(Encoding.ASCII.GetBytes("ACK"));

CommResult started = await server.StartAsync();
if (!started.IsSuccess)
{
    Console.WriteLine($"cannot listen: {started.ErrorKind} {started.ErrorMessage}");
    return;
}

await server.BroadcastAsync(Encoding.ASCII.GetBytes("HELLO"), filter: session => session.IsConnected);
await server.StopAsync();
server.Dispose();
```

- `StartAsync` returns `ResourceExhausted` when the port is in use, and `ConnectionClosed` for other bind errors. The state stays `Stopped`. States: `Stopped`, `Starting`, `Running`, `Stopping`, `Faulted`.
- Admission order: `MaxSessions` → `AllowedRemoteAddresses` (IP literals only) → `TcpChannelComponents.ConnectionFilter.Accept`. A rejected connection is closed at once and logged as a warning. No session event is raised for it.
- Each accepted session completes TLS (if enabled) and the initializer within `SessionHandshakeTimeout` (10000 ms). A session that fails this handshake is closed without raising any session event.
- `SessionConnected` is raised after the handshake, once the session is in the session table. A slow handler delays that session's buffered frames and the start of its heartbeat, and it delays other server events too.
- `SessionClosed` carries a `DisconnectReason`, such as `RemoteClosed`, `IdleTimeout`, `HeartbeatFailed`, `RequestTimeout`, `SendFailed`, `ProtocolViolation` or `UserRequested` (server stop).
- `SendAsync(sessionId, payload)` returns `NotConnected` for an unknown or closed session. `BroadcastAsync(payload, filter)` returns the number of successful sends. A session whose send fails is closed by the normal send-failure rule.
- Each session is an `ITcpSession` (`IByteChannel`), so it can also send requests (for example a device-side protocol).
- `StopAsync` stops listening and closes all sessions (`UserRequested`) within `StopTimeout` (3000 ms).
- `RestartOnFault` (a `ReconnectOptions`) re-binds the listener after a fault. Without it, a listener fault leaves the state `Faulted`.
- Do not register `TcpServer` in `ChannelFactory`. It is created and disposed by the host.

### Sessions and handshake

`ChannelComponents.Initializer` (set through `TcpChannelComponents`) runs once for each accepted session, before the session is listed in `Sessions` and before `SessionConnected`. With TLS enabled it shares the `SessionHandshakeTimeout` budget (default 10 s) with the TLS handshake. Its `IByteChannel` supports `SendAsync`, `RequestAsync` and `ReceiveAsync`; it does not raise `FrameReceived`.

- Frames the peer sends during the handshake go into a backlog of up to 64 frames. `ReceiveAsync` inside the initializer can take them; the rest are dispatched in order once `SessionConnected` has been delivered. More than 64 is a protocol violation, and the session is closed with `ProtocolViolation`.
- A failed or timed-out initializer closes only that session, and no session event is raised for it. Unlike a client, a server never retries: the peer has to connect again.
- `SessionCount` and `Sessions` list the sessions that have completed their handshake. `TryGetSession(id, out session)` finds one. `ITcpSession.CloseAsync()` closes that session only, with reason `UserRequested`; inside a server event handler it returns without waiting.
- A stopped server can be started again: after `StopAsync` completes, `StartAsync` binds the same address and port.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var components = new TcpChannelComponents
{
    Initializer = new ServerHello(),    // runs for every accepted session before SessionConnected
};

var server = new TcpServer(new TcpServerConfig
{
    Port = 5000,
    SessionHandshakeTimeout = 10000,    // covers TLS (when enabled) and the initializer together
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
}, components: components);

await server.StartAsync();

// Sessions appear here only after their handshake has completed.
Console.WriteLine($"{server.SessionCount} session(s)");
foreach (ITcpSession session in server.Sessions)
    Console.WriteLine($"session {session.Id} from {session.RemoteEndPoint}");

if (server.TryGetSession(1, out ITcpSession? first))
    await first.CloseAsync();           // closes that session only; the server keeps running

await server.StopAsync();
await server.StartAsync();              // a stopped server can be started again
await server.StopAsync();
server.Dispose();

// Waits for the client's login line. Frames the client sends in the meantime are held back until SessionConnected has been delivered.
sealed class ServerHello : IConnectionInitializer
{
    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        CommResult<byte[]> login = await channel.ReceiveAsync(new RequestOptions { Timeout = 5000 }, cancellationToken);
        return login.IsSuccess ? CommResult.Success() : login.ToResult();
    }
}
```

### Per-session health probe

A shared `ChannelComponents.HealthProbe` is rejected by the server, because one instance cannot serve every session. Create one probe per session with `ChannelComponents.HealthProbeFactory`. The factory receives the session as `IByteChannel` (cast it to `ITcpSession`). It runs once per session, when the session starts its heartbeat. A factory that throws, or returns null, closes that session with `Error`. `Heartbeat.Payload` is not required when a factory is supplied.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var components = new TcpChannelComponents
{
    HealthProbeFactory = channel => new SessionPing((ITcpSession)channel),
};

var server = new TcpServer(new TcpServerConfig
{
    Port = 5000,
    Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 2000, MaxFailures = 3 },
}, components: components);

await server.StartAsync();
server.Dispose();

sealed class SessionPing : IHealthProbe
{
    private readonly ITcpSession session;

    public SessionPing(ITcpSession session) => this.session = session;

    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        CommResult<byte[]> reply = await session.RequestAsync(new byte[] { 0x7F }, cancellationToken: cancellationToken);
        return reply.IsSuccess ? CommResult.Success() : reply.ToResult();
    }
}
```

## TLS

TLS is off by default. Enable it per side. The handshake runs inside `HandshakeTimeout` (client) or `SessionHandshakeTimeout` (server), together with the initializer.

- **Client** (`TcpClientTlsOptions`): `TargetHost` (defaults to `Host`), `Protocols` (`None` = operating system chooses), `CheckCertificateRevocation` (true), `AllowUntrustedServerCertificate` (false), `ClientCertificate` (a `CertificateSource`, loaded for each connection and released when it ends).
- **Server** (`TcpServerTlsOptions`): `ServerCertificate` (loaded when the server is constructed, released in `DisposeAsync`), `ClientCertificateRequired` (mutual TLS), `Protocols`, `CheckCertificateRevocation`.
- **Certificate source** (`CertificateSource`): `StoreLocation` (LocalMachine), `StoreName` (My), `Thumbprint`, `PfxPath`, `PfxPasswordEnvironmentVariable`. With a thumbprint, the store is opened read-only and the thumbprint is cleaned first: every non-hex character is removed and case is ignored. Expired or untrusted certificates are still found. Without a thumbprint, the PFX file is loaded with `DefaultKeySet`. The password is read only from the environment variable named in `PfxPasswordEnvironmentVariable`. A missing variable is an `ArgumentException`. The configuration has no plaintext password property.
- **Missing private key** is an `ArgumentException`. Passing a certificate in `TcpChannelComponents.ClientCertificate` or `ServerCertificate` does the same. Certificates passed that way belong to the caller, and the library does not dispose them.
- **`AllowUntrustedServerCertificate`** accepts any server certificate, even with errors. It logs a warning on every connection, with the thumbprint. Use it for debugging only.
- **`TcpChannelComponents.RemoteCertificateValidation`** replaces the default check and the untrusted flag. The default accepts only a certificate with no policy errors.
- **Errors**: `AuthenticationFailed` for a failed handshake, certificate validation or login. `InvalidRequest` when a certificate cannot be loaded (thumbprint not found, environment variable missing, no private key). `Timeout` when the handshake exceeds its budget. `ConnectionClosed` for other I/O failures.
- **Known limitation**: disconnecting does not send a TLS close_notify. The channel shuts down the socket's send side only.
- DTLS (TLS over UDP) is not supported.

```csharp
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;

// Client with mutual TLS: the client certificate is found by thumbprint in the current user's store.
var clientConfig = new TcpClientChannelConfig
{
    Host = "plc.example.local",
    Port = 8443,
    HandshakeTimeout = 5000,
    Tls = new TcpClientTlsOptions
    {
        Enabled = true,
        TargetHost = "plc.example.local",
        Protocols = SslProtocols.Tls12,
        ClientCertificate = new CertificateSource
        {
            StoreLocation = StoreLocation.CurrentUser,
            StoreName = StoreName.My,
            Thumbprint = "1A2B3C4D5E6F708192A3B4C5D6E7F80912345678",
        },
    },
};

await using var client = new TcpClientChannel(clientConfig);
var connected = await client.ConnectAsync();
Console.WriteLine(connected.IsSuccess ? $"TLS active: {client.IsTlsActive}" : $"{connected.ErrorKind}: {connected.ErrorMessage}");

// Server with a PFX certificate; the password comes from an environment variable.
var server = new TcpServer(new TcpServerConfig
{
    Port = 8443,
    Tls = new TcpServerTlsOptions
    {
        Enabled = true,
        ServerCertificate = new CertificateSource
        {
            PfxPath = @"C:\certs\server.pfx",
            PfxPasswordEnvironmentVariable = "JUNEVY_PFX_PASSWORD",
        },
        ClientCertificateRequired = true,
    },
});
server.Dispose();
```

## UDP

- **Directed** (`RemoteHost` and `RemotePort`, both or neither). `SendAsync` and `RequestAsync` go to the remote. Only datagrams from that endpoint are delivered; others count in `FramesDropped`. `RemoteHost` can be a host name, resolved when the socket opens within `HandshakeTimeout`. `RequestToAsync` to another address returns `InvalidRequest` before anything is sent. `SendToAsync` can still send one-way anywhere.
- **Undirected** (no remote). Use `SendToAsync` and `RequestToAsync`. `SendAsync` and `RequestAsync` return `InvalidRequest`. `RequestToAsync` accepts a reply only from the address it asked.
- `RequestRetryCount` (default 0) resends the same datagram after each timeout. Any reply completes the request. After the last attempt it becomes `Timeout`, and the late-reply window applies.
- An empty payload sent with `SendAsync` produces a zero-length datagram. A request with an empty payload returns `InvalidRequest`.
- A datagram larger than `MaxDatagramSize` (default 65507, allowed range 1–65507) is dropped and counted in `ProtocolErrors`.
- UDP has no framing: a `ChannelComponents.FrameCodec` is rejected with `ArgumentException`.
- Broadcast needs `EnableBroadcast`. Multicast uses `MulticastGroups` (each must be a multicast address), `MulticastTimeToLive` (1) and `MulticastLoopback` (false). With loopback off, this host does not receive its own multicast.
- Windows reports error 10054 on the next receive after a datagram hits a closed port. The channel disables that behaviour with `SIO_UDP_CONNRESET`: always on net472, and on net8.0 when running on Windows.
- Heartbeat: directed mode uses the built-in probe, which needs `Payload` and `ExpectedReply`. Undirected mode requires `HealthProbe` or `HealthProbeFactory`.
- "Connected" means the socket is bound. Nothing is sent on connect.

```csharp
using System.Net;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Udp;

// Directed: a device on a known address.
await using var directed = new UdpChannel(new UdpChannelConfig
{
    RemoteHost = "192.168.1.50",
    RemotePort = 4000,
    RequestTimeout = 1000,
    RequestRetryCount = 2,
    Heartbeat = new HeartbeatOptions { Enabled = true, Payload = "hex:00", ExpectedReply = "hex:01" },
});
await directed.ConnectAsync();
CommResult<byte[]> status = await directed.RequestAsync(Encoding.ASCII.GetBytes("STATUS?"));

// Undirected: discovery by broadcast and multicast, and requests to any address.
await using var discovery = new UdpChannel(new UdpChannelConfig
{
    LocalPort = 5000,
    EnableBroadcast = true,
    MulticastGroups = new[] { "239.1.2.3" },
});
await discovery.ConnectAsync();
discovery.FrameReceived += (sender, e) => Console.WriteLine($"{e.RemoteEndPoint}: {e.Data.Length} bytes");
await discovery.SendToAsync(new IPEndPoint(IPAddress.Broadcast, 5000), Encoding.ASCII.GetBytes("WHO_IS_THERE"));
CommResult<byte[]> answer = await discovery.RequestToAsync(new IPEndPoint(IPAddress.Parse("192.168.1.60"), 4000), Encoding.ASCII.GetBytes("READ 100"));
```

## Serial

- **One channel per physical port.** A second `SerialChannel` on the same COM port cannot open it. That open retries "access denied" until `OpenTimeout`, then fails with `ConnectionClosed`. RS-485 buses are shared through aliases of one channel in `ChannelFactory`. The protocol layer tells slaves apart by address.
- **Open**: `OpenTimeout` (2000 ms) is the total budget. "Access denied" is retried every 20 ms within it, because the driver releases the port asynchronously. Other failures (port missing, port busy) return `ConnectionClosed` at once, and the message names the port and carries the original error. A timeout returns `Timeout`, and the port is released when the driver returns. The input buffer is discarded after a successful open.
- **Framing** defaults to `IdleGap` with `GapTimeout` 20 ms. See the IdleGap note in Framing.
- **Buffers**: `ReadBufferSize` and `WriteBufferSize` must be positive and even (`SerialPort` rejects odd sizes). They set the driver's buffers only. They do not change how many bytes the channel reads at once.
- **`ResetOnRequestTimeout` is fixed to false**. A request timeout never re-opens the port. Replies that arrive in the late-reply window are dropped.
- **`PartialFrameAction` is `Discard`**. Residual bytes of a stalled frame are discarded after `PartialFrameTimeout` and the port stays open.
- **Heartbeat**: `ExpectedReply` (or `HealthProbe` or `HealthProbeFactory`) is mandatory. A serial write almost always succeeds, so a send alone proves nothing.
- **Reconnect** closes and re-opens the port with the **configured** name. A re-plugged USB adapter that gets a new COM number is not found; the channel stays in Reconnecting.
- **Net472**: `SerialPort.BaseStream` ignores cancellation tokens. A write that exceeds `SendTimeout` or is cancelled is handled by closing the port.

```csharp
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Serial;

// Delimited ASCII device, with an explicit open budget and a request timeout.
await using var scanner = new SerialChannel(new SerialChannelConfig
{
    PortName = "COM3",
    BaudRate = 115200,
    OpenTimeout = 2000,
    RequestTimeout = 1000,
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
});
CommResult opened = await scanner.ConnectAsync();
CommResult<byte[]> reply = await scanner.RequestAsync(Encoding.ASCII.GetBytes("MEASURE"));

// Binary device with a longer gap for an FTDI adapter, reconnect from the first attempt, and a heartbeat that must match a reply.
await using var binary = new SerialChannel(new SerialChannelConfig
{
    PortName = "COM7",
    BaudRate = 19200,
    Framing = new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = 30 },
    Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 1000, MaxFailures = 3, Payload = "PING", ExpectedReply = "PONG" },
    Reconnect = new ReconnectOptions { Enabled = true, OnInitialFailure = true, Interval = 2000, MaxInterval = 10000 },
});
await binary.ConnectAsync();
```

### USB-serial hot-plug: manual checklist (待人工验证 — pending manual verification)

Hot-plug has not been verified. The design identifies a risk: on net472, unplugging an open USB-serial port may raise an unhandled exception inside `SerialPort` and end the process. Do not rely on hot-plug until this checklist has been run. Record the result in `CHANGELOG.md`.

1. Preconditions: one USB-to-serial adapter, either looped back or connected to a device. Build two temporary console applications, one on net472 and one on net8.0, and run them one at a time on the same adapter.
2. Configuration: `BaudRate` 115200; `Framing` = `Delimiter` with `\r\n`; reconnect enabled with `Mode = FixedInterval` and `Interval = 1000`. Subscribe to `StateChanged`, `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, and log all of them.
3. Steps:
   1. Connect and confirm a request/reply round trip works.
   2. While idle, unplug and wait 10 seconds. Expected: the process is alive, and `Connected` → `Reconnecting` is observed.
   3. While a read is pending (for example `ReceiveAsync` waiting for an unsolicited frame), unplug. Same expectations as step 2.
   4. While a request is in flight, unplug. Expected: the request returns `ConnectionClosed` or `SendFailed`, not a timeout after the request timeout.
   5. Re-plug. If the COM number is unchanged, expected: `Connected` within one backoff interval. If the COM number changed, expected: still `Reconnecting`. That is a known limitation.
   6. While unplugged, call `DisposeAsync`, then synchronous `Dispose`. Expected: both return within about 1 second, with no crash, and another program can then open the port.
   7. Repeat steps 2–4 twenty times each, on both targets.
4. If net472 crashes, test these two guards in order, one at a time:
   - a. After opening, call `GC.SuppressFinalize` on `BaseStream`. Before closing, call `GC.ReRegisterForFinalize`.
   - b. Before closing the port, stop the fill loop and wait until it exits.

## Modbus and Channels: Differences

| | Modbus (`Junevy.Communication.Modbus`) | Channels (this skill) |
|---|---|---|
| Reconnect | Lazy: happens at the next request, only when `Reconnect = true` | Background: starts when the link is lost, when `Reconnect.Enabled` is true |
| After `Disconnect` | With `Reconnect = true`, the next request silently reconnects | `DisconnectAsync` stops reconnection until `ConnectAsync` is called |
| Result type | `ModbusResult<T>` | `CommResult` / `CommResult<T>` |
| Error kinds | `ModbusErrorKind` (0–7 align numerically with `CommErrorKind`) | `CommErrorKind` (adds `NotConnected`, `AuthenticationFailed`, `ResourceExhausted`, `NotSupported`) |
| Requests while disconnected | Attempt the connection (depending on `Reconnect`) | Return `NotConnected` at once |
| Events | none | `StateChanged`, `FrameReceived` on thread-pool threads |

## Extension Points

### Protocol packages: `ChannelComponents`

A protocol package declares how its frames are split, how replies are matched, what happens after connecting, and how liveness is checked. The values override the matching configuration properties. Reconnect still depends on `Reconnect.Enabled`.

- `FrameCodec` (`IFrameCodecFactory`): replaces `Framing`. Not accepted by UDP.
- `Correlation` and `KeyExtractor` (`IFrameKeyExtractor`): `Keyed` needs the extractor.
- `Initializer` (`IConnectionInitializer`): runs on every connection, including every reconnect. Its frames go through the handshake backlog.
- `HealthProbe` (`IHealthProbe`): replaces the built-in probe. A probe that needs the channel itself uses `HealthProbeFactory` instead (client: called once at the first successful open; server: once per session). The two are mutually exclusive.
- `ReconnectPolicy` (`IBackoffPolicy`): replaces the backoff derived from `ReconnectOptions`.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var components = new TcpChannelComponents
{
    FrameCodec = FrameCodecFactory.Create(new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 4, LengthFieldSize = 2 }),
    Correlation = CorrelationMode.Keyed,
    KeyExtractor = new SequenceKeyExtractor(),
    Initializer = new LoginInitializer(),
};

var device = new TcpClientChannel(new TcpClientChannelConfig { Host = "192.168.1.100", Port = 5000, Reconnect = new ReconnectOptions { Enabled = true } }, components: components);
await device.DisposeAsync();

sealed class LoginInitializer : IConnectionInitializer
{
    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        CommResult<byte[]> reply = await channel.RequestAsync(new byte[] { 0x01, 0x00 }, cancellationToken: cancellationToken);
        return reply.IsSuccess ? CommResult.Success() : reply.ToResult();
    }
}

// Two-byte big-endian sequence number at offset 0.
sealed class SequenceKeyExtractor : IFrameKeyExtractor
{
    public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key) => Read(request, out key);

    public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key) => Read(frame, out key);

    private static bool Read(ReadOnlySpan<byte> data, out long key)
    {
        if (data.Length < 2)
        {
            key = 0;
            return false;
        }

        key = (data[0] << 8) | data[1];
        return true;
    }
}
```

### Custom heartbeat probe: `HealthProbeFactory`

A probe that needs the channel is created by `ChannelComponents.HealthProbeFactory`, because the channel does not exist when the components are built. Client channels (TCP, UDP, serial) call the factory once, at the first successful open, before the heartbeat starts, and reuse the probe across reconnects. A factory that throws, or returns null, makes that open fail with `Unspecified`. The factory runs only when `Heartbeat.Enabled` is true. `HealthProbe` and `HealthProbeFactory` are mutually exclusive: setting both throws `ArgumentException` when the channel is constructed.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var components = new TcpChannelComponents
{
    HealthProbeFactory = channel => new PingProbe(channel),
};

var config = new TcpClientChannelConfig
{
    Host = "192.168.1.100",
    Port = 5000,
    Heartbeat = new HeartbeatOptions { Enabled = true, Interval = 5000, Timeout = 2000, MaxFailures = 3 },   // no Payload needed
    Reconnect = new ReconnectOptions { Enabled = true },
};

await using var device = new TcpClientChannel(config, components: components);
await device.ConnectAsync();

// The protocol's own ping. The factory passes the channel itself, so the probe can send requests.
sealed class PingProbe : IHealthProbe
{
    private readonly IByteChannel channel;

    public PingProbe(IByteChannel channel) => this.channel = channel;

    public async Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
    {
        CommResult<byte[]> reply = await channel.RequestAsync(new byte[] { 0x7F }, new RequestOptions { Timeout = 1000 }, cancellationToken);
        return reply.IsSuccess ? CommResult.Success() : reply.ToResult();
    }
}
```

### Custom byte-stream transport: `StreamClientChannel`

`StreamClientChannel` is the public base class for any transport exposed as a `Stream`. The base provides the lifecycle, framing, correlation, heartbeat, handshake and reconnect. A subclass implements:

- `OpenStreamAsync(CancellationToken)`: called on every connection attempt. Enforce your own connect timeout. Let a user cancellation throw, and return `CommResult<Stream>.Fail(...)` for other failures.
- `AbortTransport()`: must be idempotent and callable from any thread. It must unblock pending reads and writes, because stream I/O on net472 ignores cancellation tokens.
- Optional: `OnClosingAsync(CancellationToken)` (graceful step, at most `DisconnectTimeout`), `SecureStreamAsync(Stream, CancellationToken)` (wrap, for example in TLS, inside `HandshakeTimeout`), `PartialFrameAction` (default `Disconnect`; `Discard` keeps the connection), and `DescribeEndpoint()` (for logs).
- Constructor: `base(name, ClientChannelSettings, ChannelComponents?, ILogger)`.
- `ClientChannelSettings` timeout defaults equal the TCP client defaults: handshake 5000 ms, send 2000 ms, request 2000 ms, disconnect 1000 ms, late-reply window −1 (equal to the request timeout). **Zero still means no limit**: set the values your protocol needs explicitly.
- Do not depend on the internal types (`ConnectionSupervisor`, `StreamChannel`, `FrameRouter`). Datagram transports are internal to the Udp package.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class SkeletonChannel : StreamClientChannel
{
    public SkeletonChannel(string name, ClientChannelSettings settings, ChannelComponents? components = null)
        : base(name, settings, components, NullLogger.Instance)
    {
    }

    protected override Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
        => Task.FromResult(CommResult<Stream>.Fail("Opening this transport is not implemented.", CommErrorKind.NotSupported));

    protected override void AbortTransport()
    {
        // Close the underlying transport here. Must be idempotent.
    }

    protected override string DescribeEndpoint() => Name;
}
```

## Troubleshooting

| Symptom (result or log) | Likely cause | What to check |
|---|---|---|
| `ConnectAsync` → `Timeout` (TCP) | Host unreachable, firewall, SYNs dropped | `ConnectTimeout`, `Host`, `Port`, routing |
| `ConnectAsync` → `ConnectionClosed` (TCP) | Refused, DNS failure, nothing listening | The message contains the `SocketError`; the port; the service |
| `ConnectAsync` → `ConnectionClosed` (serial) | Port missing, busy, or access denied past `OpenTimeout` | The message names the port and the original error; the COM number in Device Manager |
| `ConnectAsync` → `Timeout` (serial) | The driver blocked the open | `OpenTimeout`; the adapter's driver |
| `ConnectAsync` → `AuthenticationFailed` | TLS certificate validation or login failed | The trust chain, `TargetHost`, revocation, the server certificate |
| `ConnectAsync` → `InvalidRequest` with TLS | Certificate could not be loaded (thumbprint not found, variable missing, no private key) | `CertificateSource`, the variable name, the private key |
| `SendAsync` / `RequestAsync` → `NotConnected` | Not connected, or reconnecting | `State` and `StateChanged`; `ConnectAsync` or `WaitForConnectedAsync` |
| `RequestAsync` → `Timeout` and the TCP connection drops | `ResetOnRequestTimeout = true` (default) | A slow device: raise `RequestTimeout`, or set `ResetOnRequestTimeout = false` with a `LateReplyWindow` |
| `RequestAsync` → `ProtocolViolation` | Frame does not match the framing (delimiter, offset, byte order), or it exceeds `MaxFrameLength` | `Framing`; a logger enabled at `Debug` (see Logging) shows each RX frame in hex |
| Serial replies arrive in pieces | `GapTimeout` too small for the adapter's latency timer | Raise `GapTimeout` to 30–50 ms |
| ASCII replies are never delivered (TCP) | No `Delimiter` framing (Raw is the TCP default) | `Framing`, `Delimiters` |
| `HeartbeatFailed` repeats | `ExpectedReply` differs from the delivered frame (delimiter stripped, or a different byte) | The RX hex logged at `Debug` (see Logging), against `ExpectedReply` |
| Unexpected disconnect with `IdleTimeout` | No inbound frame for `IdleTimeout` ms (outbound does not count) | The device's polling interval versus `IdleTimeout` |
| `TcpServer.StartAsync` → `ResourceExhausted` | Port already in use | Another process; choose another port |
| `Statistics.FramesDropped` keeps rising (serial, UDP) | `FrameReceived` is slower than arrival; `DropOldest` | Make the handler faster; raise `ReceiveQueueCapacity` |
| UDP `SendAsync` / `RequestAsync` → `InvalidRequest` | Undirected channel | Use `SendToAsync` / `RequestToAsync`, or set both `RemoteHost` and `RemotePort` |
| `NotSupportedException` from `GetOrAdd` | No creator registered for the exact configuration type | `AddTcpChannels()` and friends, or `WithCreator` |
| `ArgumentException` mentioning `ExpectedReply` | Built-in heartbeat on UDP or serial without `ExpectedReply` | Set `Heartbeat.ExpectedReply`, or supply `ChannelComponents.HealthProbe` or `HealthProbeFactory` |
| `ArgumentException` mentioning `Heartbeat.Payload` | Built-in heartbeat without `Payload` | Set `Heartbeat.Payload`, or supply `HealthProbe` or `HealthProbeFactory` |
| No log output at all | The channel was constructed without a logger, or the category is not enabled at `Debug` | Pass an `ILogger<T>` (see Logging) and set `Junevy.Communication` to `Debug` |
| A server session is missing from `Sessions` after the peer connected | Its initializer has not finished or failed (the session closes silently), or `SessionHandshakeTimeout` expired | The initializer's return value and `SessionHandshakeTimeout`; a failed handshake raises no session event |
| `ArgumentException` on a server with `HealthProbe` | The server rejects a shared probe | `ChannelComponents.HealthProbeFactory` |
| Reconnect never happens | `Reconnect.Enabled` is false, or `DisconnectAsync` was called | Enable it; call `ConnectAsync` after an intentional disconnect |
| `ObjectDisposedException` | The channel was used after `Dispose` | Create a new channel |
| Cross-thread exception in WPF | A handler touched UI objects | Marshal to the `Dispatcher` |
| Log: "Dropped a late reply" | A reply arrived after its request timed out | Expected for serial and UDP; for TCP, check `ResetOnRequestTimeout` and the device's response time |

## Common Mistakes

- Expecting a request to connect the channel. `RequestAsync` returns `NotConnected` while disconnected. Call `ConnectAsync` or enable reconnect.
- Expecting reconnect without `Reconnect.Enabled = true`. It is off by default.
- Using `Dispose` to disconnect gracefully. Call `DisconnectAsync` first, so frames already received are delivered.
- Changing the configuration object after construction. Nothing changes; create a new channel.
- Opening one serial port from two channels. Share one channel with aliases.
- Using `Raw` framing for serial.
- Leaving `ExpectedReply` unset on UDP or serial heartbeats.
- Setting a `ClientChannelSettings` timeout to zero when writing a custom transport. Zero means no limit, so a request can wait forever.
- Adding a CR LF to text passed to `SendTextAsync` or `RequestTextAsync`. The framing encoder appends the delimiter, so the device receives two.
- Setting `Matcher` on a `Keyed` request. It is ignored; the key decides.
- Indexing the request span inside a matcher. For `ReceiveAsync` it is empty.
- Assuming events run on the UI thread.
- Writing a `Delimiter` terminator into the payload. It is appended automatically.
