# Junevy.Communication.Tcp

TCP client (`TcpClientChannel`) and TCP server (`TcpServer`) on top of `Junevy.Communication.Channels`, with optional TLS for both sides. Framing, request/response correlation, heartbeat and reconnect are the same as for serial and UDP channels.

> Preview (`1.0.0-preview.1`): the public API may change before the P2 milestone (PLC protocol suite and MELSEC validation). See the root `readme.md`.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

## Installation

```text
dotnet add package Junevy.Communication.Tcp --prerelease
```

## TCP Client

Connect, send and request. The framing must match the device: here each frame ends with `\r\n`, which the encoder appends on send and the decoder removes on receive.

```csharp
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var config = new TcpClientChannelConfig
{
    Host = "192.168.1.100",
    Port = 5000,
    ConnectTimeout = 2000,
    RequestTimeout = 2000,
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
};

await using var client = new TcpClientChannel(config);
client.StateChanged += (sender, e) => Console.WriteLine($"{e.PreviousState} -> {e.CurrentState} ({e.Reason})");
client.FrameReceived += (sender, e) => Console.WriteLine($"unsolicited frame: {e.Data.Length} bytes");

CommResult connected = await client.ConnectAsync();
if (!connected.IsSuccess)
{
    Console.WriteLine($"connect failed: {connected.ErrorKind} {connected.ErrorMessage}");
    return;
}

// The delimiter is appended automatically; do not add "\r\n" to the payload yourself.
CommResult<byte[]> reply = await client.RequestAsync(Encoding.ASCII.GetBytes("READ 100"));
if (reply.IsSuccess)
    Console.WriteLine(Encoding.ASCII.GetString(reply.Data!));
else
    Console.WriteLine($"request failed: {reply.ErrorKind} {reply.ErrorMessage}");

await client.DisconnectAsync();   // graceful: drains frames already received, stops reconnecting
```

`ConnectAsync` returns a failed `CommResult` when the connection cannot be established. It throws `OperationCanceledException` only when the caller's token is cancelled.

Pass an `ILogger<TcpClientChannel>` as the second constructor argument to receive the channel's logs. See the Channels README, Logging.

## Reconnect and Heartbeat

Reconnect is off by default. When it is enabled, a lost connection is re-established in the background, and no request is needed to trigger it.

```csharp
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;

var config = new TcpClientChannelConfig
{
    Host = "192.168.1.100",
    Port = 502,
    IdleTimeout = 30000,                                   // no inbound frame for 30 s: disconnect
    Reconnect = new ReconnectOptions
    {
        Enabled = true,
        OnInitialFailure = true,                           // keep retrying even if the first connect failed
        Interval = 1000,
        MaxInterval = 30000,
    },
    Heartbeat = new HeartbeatOptions
    {
        Enabled = true,
        Interval = 5000,
        Timeout = 2000,
        MaxFailures = 3,
        Payload = "hex:00 01",                             // probe frame sent by the built-in probe
        ExpectedReply = "hex:00 02",                       // optional; without it, a successful send counts as healthy
                                                           // (Raw framing: the reply must arrive in one read)
    },
};

await using var client = new TcpClientChannel(config);
client.StateChanged += (sender, e) =>
{
    if (e.CurrentState == ConnectionState.Reconnecting)
        Console.WriteLine($"reconnecting, attempt {e.ReconnectAttempt}, reason {e.Reason}");
};

await client.ConnectAsync();
bool up = await client.WaitForConnectedAsync(timeout: 10000);
Console.WriteLine(up ? "connected" : "not connected within 10 s");
```

- After `DisconnectAsync`, nothing reconnects until you call `ConnectAsync` again.
- While the channel is not connected, `SendAsync` and `RequestAsync` return `NotConnected` immediately. They are not queued.
- With a heartbeat, the link is declared dead after `MaxFailures` consecutive failed probes. Probes start `Interval` apart, so a silent peer is detected after about `(MaxFailures − 1) × Interval + Timeout`.

## TCP Server

`TcpServer` accepts clients and gives each one an `ITcpSession`. A session has the same framing, correlation, heartbeat and events as a client channel.

```csharp
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Tcp;

var server = new TcpServer(new TcpServerConfig
{
    ListenAddress = "0.0.0.0",
    Port = 5000,
    MaxSessions = 16,                                      // 0 = unlimited
    AllowedRemoteAddresses = new[] { "192.168.1.10", "192.168.1.11" },
    Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
});

server.SessionConnected += (sender, e) => Console.WriteLine($"session {e.Session.Id} from {e.Session.RemoteEndPoint}");
server.SessionClosed += (sender, e) => Console.WriteLine($"session {e.Session.Id} closed: {e.Reason}");
server.FrameReceived += async (sender, e) =>
{
    // Reply on the same session. The frame has no delimiter; the encoder adds it.
    await e.Session.SendAsync(Encoding.ASCII.GetBytes("ACK " + Encoding.ASCII.GetString(e.Data)));
};

CommResult started = await server.StartAsync();
if (!started.IsSuccess)
{
    Console.WriteLine($"cannot listen: {started.ErrorKind} {started.ErrorMessage}");
    return;
}

Console.WriteLine($"listening on {server.LocalEndPoint}; press Enter to stop");
Console.ReadLine();
await server.BroadcastAsync(Encoding.ASCII.GetBytes("SHUTDOWN"));
await server.StopAsync();
server.Dispose();
```

`StartAsync` returns `ResourceExhausted` when the port is already in use and leaves the server `Stopped`. `StopAsync` closes all sessions, waiting at most `StopTimeout`.

Server events are raised on thread-pool threads. A session's `FrameReceived` is raised before the server-level `FrameReceived`. A slow `SessionConnected` handler delays that session's buffered frames and heartbeat, and other server events.

### Sessions and handshake

`ChannelComponents.Initializer` (set through `TcpChannelComponents`) runs once for each accepted session. It runs before the session is listed in `Sessions` and before `SessionConnected`. When TLS is enabled, it shares the `SessionHandshakeTimeout` budget with the TLS handshake. Its `IByteChannel` supports `SendAsync`, `RequestAsync` and `ReceiveAsync`; it does not raise `FrameReceived`.

- Frames the peer sends during the handshake go into a backlog of up to 64 frames. `ReceiveAsync` inside the initializer can take them. Once `SessionConnected` has been delivered, the rest are dispatched in order. More than 64 is a protocol violation: the session is closed with `ProtocolViolation`.
- If the initializer returns a failure, or the handshake times out, only that session is closed, and no session event is raised for it.
- A client runs its initializer inside `ConnectAsync` and again after every reconnect, and a failure is an open failure that the reconnect policy handles. A server does not retry: the peer has to connect again.

`SessionCount` and `Sessions` describe the sessions that have completed their handshake. `TryGetSession` finds one by `Id`. `ITcpSession.CloseAsync()` closes that session only, with reason `UserRequested`; called inside a server event handler, it returns without waiting. `StopAsync` closes every session.

A stopped server can be started again: after `StopAsync` completes, `StartAsync` binds the same address and port.

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

## Custom Session Health Probe

For a protocol-level probe, create one probe per session with `ChannelComponents.HealthProbeFactory`. Client channels use the same property; see the Channels README, Custom Heartbeat Probe. The factory receives the session (an `ITcpSession`, passed as `IByteChannel`) when the session starts its heartbeat. A shared `ChannelComponents.HealthProbe` is rejected by the server, because it would be bound to a single session.

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

// A protocol-specific probe. Heartbeat.Payload is not needed when a factory is supplied.
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

TLS is off by default and is enabled per side. The handshake runs inside `HandshakeTimeout` (client) or `SessionHandshakeTimeout` (server), together with the connection initializer.

### Client

```csharp
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;

var config = new TcpClientChannelConfig
{
    Host = "192.168.1.100",
    Port = 8443,
    HandshakeTimeout = 5000,
    Tls = new TcpClientTlsOptions
    {
        Enabled = true,
        TargetHost = "plc.example.local",                       // matched against the server certificate; defaults to Host
        Protocols = SslProtocols.Tls12,
        CheckCertificateRevocation = true,
        ClientCertificate = new CertificateSource               // mutual TLS: a certificate from the store...
        {
            StoreLocation = StoreLocation.CurrentUser,
            StoreName = StoreName.My,
            Thumbprint = "1A2B3C4D5E6F708192A3B4C5D6E7F80912345678",   // spaces and colons are ignored
        },
    },
};

await using var client = new TcpClientChannel(config);
var connected = await client.ConnectAsync();
Console.WriteLine(connected.IsSuccess ? $"TLS active: {client.IsTlsActive}" : $"{connected.ErrorKind}: {connected.ErrorMessage}");
```

A certificate loaded from a PFX file is shown in the server example below. Passwords are read from an environment variable named in the configuration. The configuration itself cannot hold a password.

### Server

```csharp
using Junevy.Communication.Tcp;

var server = new TcpServer(new TcpServerConfig
{
    Port = 8443,
    Tls = new TcpServerTlsOptions
    {
        Enabled = true,
        ServerCertificate = new CertificateSource
        {
            PfxPath = @"C:\certs\server.pfx",                             // ...or a PFX file...
            PfxPasswordEnvironmentVariable = "JUNEVY_PFX_PASSWORD",       // ...whose password comes from an environment variable
        },
        ClientCertificateRequired = false,                                // true = mutual TLS
    },
});
```

`TcpServer` loads its certificate in the constructor, so a missing file, missing environment variable or missing private key fails there with `ArgumentException`.

## Timeouts

| Setting | Default | Meaning |
|---|---|---|
| `ConnectTimeout` | 2000 ms | One TCP connect attempt; applies to every reconnect attempt |
| `HandshakeTimeout` | 5000 ms | TLS handshake plus `IConnectionInitializer`, one shared budget |
| `SendTimeout` | 2000 ms | Writing one whole frame |
| `RequestTimeout` | 2000 ms | From frame written to matching reply; per call with `RequestOptions.Timeout` |
| `ResetOnRequestTimeout` | true | A request timeout drops the connection (Sequential mode); set false to keep it and use the late-reply window |
| `LateReplyWindow` | −1 (= `RequestTimeout`) | Replies arriving within this time after a timeout are dropped |
| `IdleTimeout` | 0 (off) | No inbound frame for this long disconnects |
| `PartialFrameTimeout` | 0 (off) | An incomplete frame held this long disconnects |
| `DisconnectTimeout` | 1000 ms | Graceful close: frames already received are delivered within this time |
| `Heartbeat.Interval` / `Timeout` / `MaxFailures` | 5000 / 2000 / 3 | Liveness probe |
| `Reconnect.Interval` / `MaxInterval` | 1000 / 30000 ms | Backoff between reconnect attempts (only when `Reconnect.Enabled`) |
| Server `SessionHandshakeTimeout` | 10000 ms | TLS and initializer for each accepted session |
| Server `SessionIdleTimeout` | 0 (off) | A session with no inbound data for this long is closed |
| Server `StopTimeout` | 3000 ms | `StopAsync` waits this long for sessions to close |

## Socket Options

`TcpClientChannelConfig.Socket` (and `TcpServerConfig.Socket`) sets `NoDelay` (default true), receive and send buffer sizes (0 = system default), `LingerTime` (−1 = system default) and TCP keep-alive. Keep-alive defaults: enabled, first probe after 30 s, interval 5 s, 3 retries.

## Platform Notes

- **net8.0**: stream and socket operations honour cancellation tokens. Keep-alive idle time and interval are set in whole seconds (milliseconds rounded up), and the retry count is applied.
- **net472**: `NetworkStream`, `SslStream` authentication and `Dns` ignore cancellation tokens. A write or TLS handshake that exceeds its deadline is cancelled by closing the socket, which fails the operation the same way. A DNS lookup that exceeds `ConnectTimeout` is abandoned and its result discarded.
- **net472 keep-alive**: only the idle time and interval are set (`SIO_KEEPALIVE_VALS`). `KeepAlive.RetryCount` is ignored; the system decides the retry count.

## Not Supported

- DTLS (TLS over UDP) is not available; the BCL does not implement it.
- TLS close_notify on disconnect: the channel shuts down the socket's send side but does not send a TLS close alert.

## More

For the complete behavioural contract, including late replies, the heartbeat detection time, and how the server admits sessions, see `Skills/using-junevy-channels/SKILL.md` in the repository.
