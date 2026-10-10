# Junevy.Communication.Udp

UDP channel (`UdpChannel`) on top of `Junevy.Communication.Channels`. Each datagram is one frame, so no framing is needed. Two modes are available:

- **Directed** (`RemoteHost` and `RemotePort` set): a point-to-point channel. `SendAsync` and `RequestAsync` talk to the remote endpoint, and only datagrams from that endpoint are delivered.
- **Undirected** (no remote set): the channel binds a local port and uses `SendToAsync` and `RequestToAsync` with any address. Use it for discovery, broadcast and multicast, or to act as a UDP "server".

> Preview (`1.0.0-preview.1`): the public API may change before the P2 milestone (PLC protocol suite and MELSEC validation). See the root `readme.md`.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

## Installation

```text
dotnet add package Junevy.Communication.Udp --prerelease
```

## Directed Channel

UDP has no delivery guarantee, so requests can be resent. `RequestRetryCount` sends the same datagram again after each timeout, and any reply to any attempt completes the request.

```csharp
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Udp;

var config = new UdpChannelConfig
{
    LocalPort = 0,                          // 0 = let the system choose the local port
    RemoteHost = "192.168.1.50",
    RemotePort = 4000,
    RequestTimeout = 1000,                  // per attempt
    RequestRetryCount = 2,                  // up to three attempts in total
    Heartbeat = new HeartbeatOptions
    {
        Enabled = true,
        Interval = 5000,
        Timeout = 1000,
        MaxFailures = 3,
        Payload = "hex:00",                 // sent to the remote endpoint
        ExpectedReply = "hex:01",           // required for UDP: a send alone proves nothing
    },
};

await using var udp = new UdpChannel(config);
CommResult bound = await udp.ConnectAsync();          // binds the socket; no packet is sent yet
if (!bound.IsSuccess)
{
    Console.WriteLine(bound.ErrorMessage);
    return;
}

CommResult<byte[]> reply = await udp.RequestAsync(Encoding.ASCII.GetBytes("STATUS?"));
Console.WriteLine(reply.IsSuccess ? Encoding.ASCII.GetString(reply.Data!) : $"{reply.ErrorKind}: {reply.ErrorMessage}");
```

- `RemoteHost` may be an IP literal or a host name. A host name is resolved when the socket is opened, within `HandshakeTimeout`.
- `RemoteHost` and `RemotePort` must be set together or not at all.
- Datagrams from other endpoints are counted in `Statistics.FramesDropped` and not delivered.

## Undirected Channel: Broadcast and Multicast

```csharp
using System.Net;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Udp;

var config = new UdpChannelConfig
{
    LocalAddress = "0.0.0.0",
    LocalPort = 5000,
    EnableBroadcast = true,                         // required to send to 255.255.255.255 or a subnet broadcast
    MulticastGroups = new[] { "239.1.2.3" },        // joined when the socket opens; must be multicast addresses
    MulticastLoopback = false,                      // true = also receive what this host multicasts
};

await using var udp = new UdpChannel(config);
udp.FrameReceived += (sender, e) => Console.WriteLine($"{e.RemoteEndPoint}: {e.Data.Length} bytes");

await udp.ConnectAsync();

CommResult sent = await udp.SendToAsync(new IPEndPoint(IPAddress.Broadcast, 5000), Encoding.ASCII.GetBytes("WHO_IS_THERE"));
Console.WriteLine(sent.IsSuccess ? "discovery sent" : sent.ErrorMessage);
```

`ReceiveAsync` waits for the next datagram from any source, and `FrameReceived` delivers every unclaimed datagram.

An undirected channel cannot use the built-in heartbeat, because that probe is sent to a remote endpoint. Enable `Heartbeat` only with `ChannelComponents.HealthProbe` or `ChannelComponents.HealthProbeFactory`. The factory receives the channel when the socket first opens. See the Channels README, Custom Heartbeat Probe.

## Undirected Request to One Address

`RequestToAsync` sends to the address you give and accepts a reply only from that same address. A reply from anywhere else is not delivered.

```csharp
using System.Net;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Udp;

await using var udp = new UdpChannel(new UdpChannelConfig { LocalPort = 0, RequestTimeout = 1500 });
await udp.ConnectAsync();

var device = new IPEndPoint(IPAddress.Parse("192.168.1.60"), 4000);
CommResult<byte[]> reply = await udp.RequestToAsync(device, Encoding.ASCII.GetBytes("READ 100"));
Console.WriteLine(reply.IsSuccess ? $"{reply.Data!.Length} bytes" : $"{reply.ErrorKind}: {reply.ErrorMessage}");
```

In directed mode, `SendToAsync` can still send to another address (one-way). `RequestToAsync` to another address returns `InvalidRequest` before anything is sent.

## Timeouts

| Setting | Default | Meaning |
|---|---|---|
| `HandshakeTimeout` | 5000 ms | `IConnectionInitializer` and host-name resolution, one shared budget |
| `SendTimeout` | 2000 ms | Writing one datagram |
| `RequestTimeout` | 2000 ms | Per attempt; `RequestOptions.Timeout` overrides it per call |
| `RequestRetryCount` | 0 | Extra attempts after a timeout; the same datagram is sent again |
| `LateReplyWindow` | −1 (= `RequestTimeout`) | Replies arriving after a request has timed out are dropped for this long |
| `IdleTimeout` | 0 (off) | No inbound datagram for this long disconnects |
| `DisconnectTimeout` | 1000 ms | Graceful close: datagrams already received are delivered within this time |
| `Heartbeat.Interval` / `Timeout` / `MaxFailures` | 5000 / 2000 / 3 | Liveness probe (directed mode sends it to the remote) |
| `Reconnect.Interval` / `MaxInterval` | 1000 / 30000 ms | Backoff; reconnect re-binds the socket |

A timed-out request does not drop the socket. In Sequential mode (the default), the late-reply window keeps the request lock held, so the next request does not mistake a late reply for its own reply.

## Other Settings

- `MaxDatagramSize` (default 65507): larger datagrams are dropped and counted in `Statistics.ProtocolErrors`. The .NET socket returns whole datagrams and cannot report truncation, so lower this to the largest reply your device sends, and oversized packets are rejected instead of delivered.
- `ReceiveBufferSize` (default 65536): the socket receive buffer.
- `ReuseAddress`, `MulticastTimeToLive` (default 1), `MulticastLoopback` (default false).
- `ReceiveQueueCapacity` (default 1024) and `QueueFullMode` (default `DropOldest`): UDP has no flow control, so a full queue drops the oldest frame and counts it in `Statistics.FramesDropped`.

## Platform Notes

- **Windows (10054)**: sending to a port with no listener can make the next receive fail with `SocketException` 10054. The channel disables this behaviour with `SIO_UDP_CONNRESET`: always on `net472`, and on `net8.0` when running on Windows.
- **net472**: `UdpClient.ReceiveAsync()` ignores cancellation tokens. Stopping the channel closes the client, which ends the receive loop.
- **net8.0**: receive and send honour cancellation tokens.

## Not Supported

- DTLS. The BCL does not implement it.
- `ChannelComponents.FrameCodec`: a datagram is already one frame, so a framing codec is rejected with `ArgumentException`.

## More

For the complete behavioural contract, including the directed-mode source filter, request retry and the undirected heartbeat rule, see `Skills/using-junevy-channels/SKILL.md` in the repository.
