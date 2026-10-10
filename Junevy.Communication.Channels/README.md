# Junevy.Communication.Channels

The byte-channel family: the abstractions (`IClientChannel`, `IByteChannel`), framing, request/response correlation, the connection lifecycle (background reconnect, heartbeat, idle timeout), and the `ChannelFactory` that creates channels by name.

Applications normally use the transport packages (`Junevy.Communication.Tcp`, `.Udp`, `.Serial`), which depend on this package. Reference it directly when you write a protocol package (framing, correlation, handshake and heartbeat supplied through `ChannelComponents`) or a custom transport (a `StreamClientChannel` subclass).

> Preview (`1.0.0-preview.1`): the public API may change before the P2 milestone (PLC protocol suite and MELSEC validation). See the root `readme.md`.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

## Installation

```text
dotnet add package Junevy.Communication.Channels --prerelease
```

## Main Types

| Type | Role |
|---|---|
| `IConnectable` / `IByteChannel` / `IClientChannel` | Link state and events; `SendAsync`, `RequestAsync`, `ReceiveAsync`, `FrameReceived`; the client-side combination |
| `ConnectionState`, `DisconnectReason`, `ConnectionStateChangedEventArgs` | Lifecycle state, why it changed, and the reconnect attempt number |
| `RequestOptions` | Per-call `Timeout` and `Matcher` (`IResponseMatcher`) |
| `FramingOptions`, `FramingMode`, `LengthFieldEncoding` | Declarative framing; `FrameCodecFactory.Create` turns it into a codec |
| `CorrelationMode` | `Sequential`, `Matcher` or `Keyed` request/response correlation |
| `IFrameKeyExtractor` | Extracts the correlation key of requests and responses (`Keyed` mode) |
| `IConnectionInitializer` | Handshake hook that runs before a connection reports `Connected` |
| `IHealthProbe` | Application-level liveness probe |
| `HeartbeatOptions`, `ReconnectOptions` | Built-in probe and background reconnect settings |
| `ChannelComponents` | Code-level overrides for framing, correlation, handshake, probe and reconnect policy |
| `StreamClientChannel` | Public base class for byte-stream transports (extension point) |
| `ChannelFactory`, `ChannelFactoryBuilder`, `IChannelCreator` | Named channel registry with aliases; one creator per configuration type |
| `AddChannels()` | Registers `ChannelFactory` in `Microsoft.Extensions.DependencyInjection` (`Junevy.Communication.Channels.DependencyInjection`) |

## Framing

Pick the framing from the device's wire format:

| `FramingMode` | Parameters | Typical use |
|---|---|---|
| `Raw` | none | Packet capture; each read is one frame (not for serial) |
| `Delimiter` | `Delimiters`, `KeepDelimiter` | Barcode readers, vision, ASCII commands |
| `FixedLength` | `FrameLength` | Fixed-size records |
| `LengthField` | `LengthFieldOffset`, `LengthFieldSize`, `LengthFieldEncoding`, `LengthAdjustment`, `InitialBytesToStrip` | Modbus TCP, S7, MELSEC, SECS HSMS, custom binary or ASCII protocols |
| `StartEnd` | `StartMarker`, `EndMarker`, `KeepMarkers` | STX…ETX frames; garbage bytes are skipped |
| `IdleGap` | `GapTimeout` (default 20 ms) | Serial devices with no delimiter or length field |

The total length of a `LengthField` frame is `offset + size + value + adjustment`.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

var delimited = new FramingOptions
{
    Mode = FramingMode.Delimiter,
    Delimiters = new[] { "\r\n" },    // text escapes (\r \n \t \0 \\ \xHH) or hex:0D0A
    KeepDelimiter = false,
    MaxFrameLength = 4096,
};

IFrameCodecFactory codec = FrameCodecFactory.Create(delimited);
IFrameDecoder decoder = codec.CreateDecoder();   // one decoder per connection
IFrameEncoder encoder = codec.CreateEncoder();
```

`FrameCodecFactory.Create` validates the options and copies them; changing the object afterwards has no effect.

## Using the Factory Without DI

`ChannelFactoryBuilder` mirrors `ModbusFactoryBuilder`. The factory knows nothing about transports until a creator is registered. Creators are selected by the exact configuration type.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;

using ChannelFactory factory = ChannelFactoryBuilder.Create()
    .WithCreator(new TcpClientChannelCreator())
    .WithCreator(new SerialChannelCreator())
    .Build();

IClientChannel plc = factory.GetOrAdd("plc-1", new TcpClientChannelConfig { Host = "192.168.1.100", Port = 502 });
IClientChannel scanner = factory.GetOrAdd("scanner", new SerialChannelConfig { PortName = "COM4", BaudRate = 9600 });

factory.RegisterAlias("scanner-main", "scanner");   // the alias shares the instance
```

`GetOrAdd` returns the existing channel when the name is already registered; its configuration argument is then ignored.

## Using the Factory With Dependency Injection

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;
using Junevy.Communication.Tcp.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddTcpChannels();                       // AddChannels() + the TCP creator; idempotent

using ServiceProvider provider = services.BuildServiceProvider();
ChannelFactory factory = provider.GetRequiredService<ChannelFactory>();

IClientChannel plc = factory.GetOrAdd("plc-1", new TcpClientChannelConfig { Host = "192.168.1.100", Port = 502 });
```

`AddChannels()` registers the factory as a singleton. Transport packages add their creators (`AddTcpChannels()`, `AddUdpChannels()`, `AddSerialChannels()`).

## Protocol Components

A protocol package declares how its frames are split, how replies are matched, what runs after connecting and how liveness is checked, and passes that to the channel. `ChannelComponents` values override the matching configuration properties, and reconnect still requires `Reconnect.Enabled`.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var components = new ChannelComponents
{
    FrameCodec = FrameCodecFactory.Create(new FramingOptions
    {
        Mode = FramingMode.LengthField,
        LengthFieldOffset = 4,           // Modbus TCP style: length in bytes 4-5
        LengthFieldSize = 2,
        MaxFrameLength = 1024,
    }),
    Correlation = CorrelationMode.Keyed,
    KeyExtractor = new SequenceKeyExtractor(),
    Initializer = new LoginInitializer(),
};

using ChannelFactory factory = ChannelFactoryBuilder.Create()
    .WithCreator(new TcpClientChannelCreator())
    .Build();

IClientChannel device = factory.GetOrAdd(
    "device-1",
    new TcpClientChannelConfig { Host = "192.168.1.100", Port = 5000, Reconnect = new ReconnectOptions { Enabled = true } },
    components);

// Runs after every connection is established, before it reports Connected.
sealed class LoginInitializer : IConnectionInitializer
{
    public async Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
    {
        CommResult<byte[]> reply = await channel.RequestAsync(new byte[] { 0x01, 0x00 }, cancellationToken: cancellationToken);
        return reply.IsSuccess ? CommResult.Success() : reply.ToResult();
    }
}

// The correlation key is the two-byte big-endian sequence number at offset 0.
sealed class SequenceKeyExtractor : IFrameKeyExtractor
{
    public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key) => TryReadSequence(request, out key);

    public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key) => TryReadSequence(frame, out key);

    private static bool TryReadSequence(ReadOnlySpan<byte> data, out long key)
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

`Keyed` mode requires a `KeyExtractor`. Without one, the channel constructor throws `ArgumentException`.

## Text Protocols

Barcode readers, vision systems and many controllers exchange text lines. `ByteChannelTextExtensions` adds `SendTextAsync`, `RequestTextAsync` and `ReceiveTextAsync` to every `IByteChannel`: client channels, server sessions and UDP channels.

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

- The text is encoded as UTF-8 without a BOM, unless you pass an `Encoding`.
- Nothing is appended to the text. The framing encoder appends `Delimiters[0]` once (`AppendDelimiterOnSend`, on by default). Adding a terminator yourself makes the device receive two.
- Replies are decoded with the same encoding and do not include the delimiter.
- A failed result keeps its `ErrorKind`, message, protocol code and exception.
- A `null` channel or text throws `ArgumentNullException`.

## Custom Byte-Stream Transport

Derive from `StreamClientChannel` to add a transport that is exposed as a `Stream` (named pipes, Bluetooth serial, and so on). The base class provides the connection lifecycle, framing, correlation, heartbeat, handshake and reconnect.

```csharp
using System.IO.Pipes;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging.Abstractions;

public static class Program
{
    public static async Task Main()
    {
        // Timeouts default to the TCP client values. A zero value means "no limit", so set the values your protocol needs.
        var settings = new ClientChannelSettings
        {
            HandshakeTimeout = 5000,
            SendTimeout = 2000,
            RequestTimeout = 2000,
            LateReplyWindow = -1,
            DisconnectTimeout = 1000,
            ResetOnRequestTimeout = true,
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\n" } },
            Reconnect = new ReconnectOptions { Enabled = true, Interval = 1000 },
        };

        await using var channel = new NamedPipeClientChannel("my-pipe", settings);
        CommResult connected = await channel.ConnectAsync();
        Console.WriteLine(connected.IsSuccess ? "connected" : connected.ErrorMessage);
    }
}

public sealed class NamedPipeClientChannel : StreamClientChannel
{
    private readonly string pipeName;
    private NamedPipeClientStream? pipe;

    public NamedPipeClientChannel(string pipeName, ClientChannelSettings settings, ChannelComponents? components = null)
        : base($"pipe://{pipeName}", settings, components, NullLogger.Instance)
    {
        this.pipeName = pipeName;
    }

    // Called for every connection attempt. Honour the cancellation token: a user cancel is thrown, other failures are returned.
    protected override async Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            pipe = client;
            return CommResult<Stream>.Success(client);
        }
        catch (TimeoutException ex)
        {
            client.Dispose();
            return CommResult<Stream>.Fail("The named pipe did not accept the connection in time.", CommErrorKind.Timeout, null, ex);
        }
        catch (IOException ex)
        {
            client.Dispose();
            return CommResult<Stream>.Fail("Opening the named pipe failed.", CommErrorKind.ConnectionClosed, null, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // Must be idempotent and must unblock pending reads and writes: on net472 stream I/O ignores cancellation tokens.
    protected override void AbortTransport()
    {
        NamedPipeClientStream? current = pipe;
        pipe = null;
        current?.Dispose();
    }

    protected override string DescribeEndpoint() => pipeName;
}
```

Override `SecureStreamAsync` to wrap the stream (for example in TLS). Override `PartialFrameAction` to return `Discard` when a partial frame should be dropped and the connection kept; the default is `Disconnect`. The serial channel uses `Discard`.

## Options and Defaults

`ClientChannelSettings` is what a `StreamClientChannel` subclass receives. Its timeout defaults equal the TCP client defaults, so a subclass that forgets to set one still gets a timeout. A zero timeout means "no limit".

| Setting | `ClientChannelSettings` default | `TcpClientChannelConfig` default |
|---|---|---|
| `HandshakeTimeout` | 5000 ms | 5000 ms |
| `SendTimeout` | 2000 ms | 2000 ms |
| `RequestTimeout` | 2000 ms | 2000 ms |
| `DisconnectTimeout` | 1000 ms | 1000 ms |
| `LateReplyWindow` | -1 (equal to `RequestTimeout`) | -1 |
| `IdleTimeout`, `PartialFrameTimeout` | 0 (disabled) | 0 (disabled) |
| `ResetOnRequestTimeout` | false: the connection is kept and the late-reply window applies | true: the connection is rebuilt |
| `ReceiveBufferSize` | 4096 | not set by the TCP configuration (base default 4096) |
| `ReceiveQueueCapacity` | 1024 | 1024 |
| `Correlation` | `Sequential` | `Sequential` |
| `QueueFullMode` | `Wait` | `Wait` (serial and UDP use `DropOldest`) |

| `HeartbeatOptions` | Default |
|---|---|
| `Enabled` | false |
| `Interval`, `Timeout`, `MaxFailures` | 5000 ms, 2000 ms, 3 |
| `OnlyWhenIdle` | true (no probe while frames are flowing) |
| `Payload` | none; required by the built-in probe unless `ChannelComponents.HealthProbe` is supplied |
| `ExpectedReply` | none (a successful send counts as healthy); compared byte for byte |

| `ReconnectOptions` | Default |
|---|---|
| `Enabled` | false |
| `Mode`, `Interval`, `MaxInterval` | `ExponentialBackoff`, 1000 ms, 30000 ms (±20 % jitter) |
| `MaxAttempts` | 0 (unlimited) |
| `OnInitialFailure` | false (a failed first `ConnectAsync` does not start background reconnection) |

## Platform Notes

- Same API on `net472` and `net8.0`.
- On `net472`, `NetworkStream` and `SerialPort.BaseStream` ignore cancellation tokens. The channel handles this by aborting the transport when a deadline or a cancellation fires. Custom transports must do the same in `AbortTransport`.

## More

For the complete behavioural contract, including the state machine and the late-reply rules, see `Skills/using-junevy-channels/SKILL.md` in the repository.
