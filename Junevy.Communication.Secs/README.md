# Junevy.Communication.Secs

SECS-II messages over an HSMS-SS session, for the **equipment** side of the link. The package owns the message model (items, bodies, SML text), the HSMS session (Select, Linktest, keepalive, single session) and the equipment-side dispatcher (handlers, S9 system messages, transaction results). Transport, framing, request/reply correlation, heartbeat and reconnect come from the channel family (`Junevy.Communication.Channels`, `Junevy.Communication.Tcp`).

> **Preview and validation status**: the encoding follows the public descriptions of SEMI E5 (SECS-II) and E37 / E37.1 (HSMS, HSMS-SS), and every frame is checked byte by byte against the vectors in the implementation plan. It has **not been verified against real equipment or a real host**. The items under [Not Yet Verified](#not-yet-verified-p51-d18) are inferred and must be checked on hardware before production use.

## Target Frameworks

`net472` and `net8.0`.

## Installation

```text
dotnet add package Junevy.Communication.Secs --prerelease
```

The package references `Junevy.Communication.Core`, `Junevy.Communication.Channels` and `Junevy.Communication.Tcp`, and adds no NuGet dependency of its own.

## Scope

- **Role**: equipment only. The host side (sending `Select.req`, the data the host originates) is not implemented.
- **Session**: HSMS-SS, one session at a time. `Passive` waits for the host to connect and select; `Active` dials the host and sends `Select.req` itself.
- **Messages**: SECS-II data messages (any stream, any function) with the S9 system messages a host expects from an equipment, plus `Linktest` as the keepalive.
- **Not supported**: SECS-I (RS-232 block transfer) and the `J` / `C2` item formats (decoding them is reported as illegal data), GEM (planned as a separate package), `Deselect` (HSMS-SS has one session, so it is rejected).

## Quick Start

```csharp
using System;
using System.Threading.Tasks;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs;
using Junevy.Communication.Secs.Hsms;

public static class EquipmentExample
{
    public static async Task RunAsync()
    {
        var options = new HsmsEquipmentOptions { Host = "0.0.0.0", Port = 5000, DeviceId = 1 };
        await using HsmsEquipment equipment = HsmsEquipment.Create(options);

        // The host sends S1F1 W; answer S1F2 with the equipment identity.
        equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(
            primary.CreateReply(SecsItem.L(SecsItem.A("EQ01"), SecsItem.A("1.0.0")))));

        CommResult started = await equipment.StartAsync();
        if (!started.IsSuccess)
            throw new InvalidOperationException($"The equipment did not start: {started.ErrorMessage}");

        // Wait for the host to select the session before sending anything.
        while (equipment.State != HsmsState.Selected)
            await Task.Delay(100);

        SecsResult sent = await equipment.SendAsync(
            SecsMessage.Create(5, 1, replyExpected: true, SecsItem.L(SecsItem.B(0x80), SecsItem.U4(1001))));

        if (!sent.IsSuccess)
            throw new InvalidOperationException($"S5F1 was not acknowledged: {sent.Error}. {sent.ErrorMessage}");

        Console.WriteLine(sent.Reply!.ToSml());
    }
}
```

`HsmsEquipment.Create` only builds the object: it does not listen or connect. `StartAsync` opens the session and returns a result; a failure (port in use, host unreachable, handshake refused) is a failed `CommResult`, never an exception. Stopping or disposing the equipment while `StartAsync` is still waiting — for example for the first `Select.rsp` in `Active` mode — ends it at once with `ConnectionClosed`; cancelling its token gives `Cancelled`.

### Active Mode

```csharp
using Junevy.Communication.Secs.Hsms;

public static class ActiveEquipmentExample
{
    public static HsmsEquipment Create()
        => HsmsEquipment.Create(new HsmsEquipmentOptions
        {
            Mode = HsmsConnectionMode.Active,
            Host = "192.168.1.10",
            Port = 5000,
            DeviceId = 1,
        });
}
```

In `Active` mode the equipment dials the host, sends `Select.req` and waits for T6. A dropped link is retried every T5, and every reconnect sends `Select.req` again, so the session becomes `Selected` again by itself. The same holds when the host ends the session with `Separate.req`: the equipment closes the connection without answering, dials again after T5 and keeps retrying every T5 until the host selects it again.

## Options

`HsmsEquipmentOptions` holds plain properties and binds directly from `appsettings.json`. `HsmsEquipment.Create` validates them and takes a snapshot, so changing the object afterwards does not affect a running equipment.

| Property | Default | Meaning |
|---|---|---|
| `Mode` | `Passive` | `Passive` listens and waits for the host; `Active` dials the host and sends `Select.req` |
| `Host` | `null` | `Passive`: the local IP address to listen on, `null` for every address. `Active`: the address of the host, **required** — an IP address or a host name |
| `Port` | `5000` | 1–65535; 5000 is the standard HSMS port |
| `DeviceId` | `0` | The equipment's HSMS session id, 0–32767. It is the session id of every data message that carries a body |
| `T3` | `45000` ms | Reply timeout: how long a primary with the W bit waits for its reply |
| `T5` | `10000` ms | Connect interval: how long `Active` waits before reconnecting |
| `T6` | `5000` ms | Control transaction timeout: `Select` and `Linktest` |
| `T7` | `10000` ms | Not-selected timeout: how long a `Passive` session waits for `Select.req` |
| `T8` | `5000` ms | Inter-character timeout: the longest gap inside one message |
| `LinktestInterval` | `0` | Keepalive interval. `0` disables `Linktest` |
| `MaxMessageLength` | `16 MiB` | Largest single message, at least 1024 bytes. It is also the largest frame the channel accepts |

Invalid values throw `ArgumentException` from `Create` (and from the factory) with the property name and the accepted range.

## State and Events

| State | Meaning |
|---|---|
| `NotConnected` | No session. Also the state after a stop, a failed handshake or a disconnect |
| `NotSelected` | The TCP connection is up, but the host has not selected the session yet |
| `Selected` | The session is selected and data messages flow |

`StateChanged` reports every change in order with a reason. TCP being connected only means bytes flow, which is why `NotSelected` is a state of its own and is not folded into the channel's `ConnectionState`. `Selected` is reported once the selected link has been taken over — when the client channel reports it is connected (`Active`) or the server reports the session (`Passive`) — right after the `Select` exchange, so a late notice about an old link can never overwrite the state of a newer one.

## Handlers

`SetHandler(stream, function, handler)` registers a handler for a data **primary** (odd function). Messages on the same session are dispatched one at a time in arrival order; a handler may be slow, but two handlers never run at the same time.

```csharp
equipment.SetHandler(6, 11, async (primary, token) =>
{
    SecsItem? body = primary.Body;
    // Do the equipment work here; honour token so a stop can interrupt it.
    return body is null ? null : primary.CreateReply(SecsItem.B(0x00));
});
```

- The handler returns the reply, or `null`. For a primary with the W bit, `null` (or a reply with the wrong shape) aborts the transaction with `SxF0`; for a primary without the W bit the return value is ignored and a non-null value is logged as a warning.
- The reply must be the same stream, function + 1 and without the W bit. The package puts the original system bytes back, so the host can correlate it.
- Exceptions are caught, logged and turn into `SxF0`; one failing handler does not stop the dispatcher.
- The token is cancelled when the equipment stops or is disposed. **A handler must honour it** — stopping waits for the running handler to return, and its reply is then not sent. A handler may call `StopAsync` itself; that does not deadlock.
- Throwing `ArgumentException` from `SetHandler` for an even function or a stream above 127 is the only validation; a message without a handler is answered with S9F3 or S9F5 instead.

## Sending

`SendAsync` returns a `SecsResult`.

| Situation | `Error` | `CommErrorKind` |
|---|---|---|
| Reply received | `None`, `Reply` is set | — |
| Host aborted the transaction (`SxF0`) | `Aborted` | `RemoteError` |
| Host sent `Reject.req` | `Rejected`, `RejectReason` carries the reason code | `RemoteError` |
| No reply within T3 (an S9F9 is sent first) | `ReplyTimeout` | `Timeout` |
| Reply cannot be decoded (an S9F7 is sent first) | `InvalidReply` | `ProtocolViolation` |
| The session is not selected | `NotSelected` | `NotConnected` |
| Stopped or disposed | `ConnectionLost` | `NotConnected` |

Several sends may be in flight at once: correlation uses the system bytes.

## S9 System Messages

A host expects these answers from an equipment. They are sent by the package, not by handlers.

| Message | When |
|---|---|
| S9F1 | The session id is not the configured `DeviceId` |
| S9F3 | The stream has no handler at all |
| S9F5 | The stream has handlers, but not for that function |
| S9F7 | The body cannot be decoded or breaks a decode limit |
| S9F9 | A primary with the W bit timed out after T3 |
| `SxF0` | The handler could not answer a primary with the W bit |

The body of an S9 message is the 10-byte header of the message it is about, as a binary item. An S9 message is a new primary of the equipment, so it carries system bytes of its own — never those of the message it is about, which the host could take for a reply.

A message whose header has a `PType` other than 0 is not SECS-II: it is answered with `Reject.req` reason 2, before and after selection, and never reaches a handler.

## SML Text

`ToSml()` renders a message or item as SML, for logs and diagnostics. It uses `\n` for line breaks on every platform, `G9` for `float` and `G17` for `double`, and writes bytes outside printable ASCII as `\xHH`. Output only — there is no SML parser.

```text
S1F2
<L [2]
  <A "EQ01">
  <A "1.0.0">
>
.
```

## Stop and Dispose

- `StopAsync` sends a best-effort `Separate.req` when the session is selected, then closes the connection and the listener (or the reconnect loop). **Stopping is not disposing**: `StartAsync` can be called again, as often as you like.
- `Dispose` / `DisposeAsync` stops and then makes the equipment permanently unusable: `StartAsync` throws `ObjectDisposedException`, `SendAsync` returns `ConnectionLost`, and `StopAsync`, disposing twice and the handler table methods do not throw.
- Both are idempotent.

## Dependency Injection

```csharp
using Junevy.Communication.Secs.DependencyInjection;
using Junevy.Communication.Secs.Hsms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class SecsDependencyInjectionExample
{
    public static void Register(IServiceCollection services)
        => services.AddSecs();

    public static HsmsEquipment Create(IServiceProvider provider, IConfiguration configuration)
    {
        HsmsEquipmentOptions options = configuration.GetSection("Secs").Get<HsmsEquipmentOptions>()
            ?? throw new InvalidOperationException("The Secs section is missing.");
        return provider.GetRequiredService<HsmsEquipmentFactory>().GetOrAdd("line-1", options);
    }
}
```

`AddSecs()` registers the `HsmsEquipmentFactory` singleton and can be called more than once. The factory reuses one equipment per name, shares it between aliases, and disposes it when it is removed or when the factory is disposed. `GetOrAdd` only builds the object; call `StartAsync` on it yourself.

```json
{
  "Secs": {
    "Mode": "Active",
    "Host": "192.168.1.10",
    "Port": 5000,
    "DeviceId": 1,
    "T3": 45000,
    "T5": 10000,
    "T6": 5000,
    "T7": 10000,
    "T8": 5000,
    "LinktestInterval": 30000,
    "MaxMessageLength": 16777216
  }
}
```

## Logging

State changes are logged at `Information`, each sent and received frame at `Debug` (direction, stream, function, W bit and system bytes — **no body**), and unexpected exceptions at `Error`. Body text in SML is reserved for `Trace`, because a body can carry recipe data.

## Not Yet Verified (P51-D18)

Five items are inferred from the public descriptions of the standards and are covered by tests against the plan's vectors only:

1. **`Select.req` uses the session id `0xFFFF`**, the common HSMS-SS practice.
2. **System bytes use the low 16 bits**, zero-extended to 4 bytes.
3. **A handler that returns `null` for a primary with the W bit is answered with `SxF0`**, not with S9F7.
4. **The `J` and `C2` item formats are not supported**; they are reported as illegal data.
5. **A T3 timeout sends S9F9 first** and then returns `ReplyTimeout`.

## Not in P5.1

SECS-I (RS-232 block transfer), the `J` and `C2` item formats, the host role, GEM (planned as the next SECS package), `Deselect`, and a public API for per-call data formats.