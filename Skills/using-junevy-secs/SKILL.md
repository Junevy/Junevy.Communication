---
name: using-junevy-secs
description: Use when a .NET project references Junevy.Communication.Secs, or needs to talk SECS-II over HSMS-SS as equipment — building SecsItem/SecsMessage bodies, registering handlers for received primaries (SetHandler), sending with SendAsync and reading SecsResult/SecsError, choosing HsmsConnectionMode Passive or Active, HsmsEquipmentOptions and the T3/T5/T6/T7/T8 timers, HsmsState and StateChanged, S9 system messages, SML text (ToSml), HsmsEquipmentFactory or AddSecs() DI registration, or troubleshooting not-selected sessions, S9F3/S9F5 replies, reply timeouts and handlers that ignore the cancellation token
---

# Using Junevy.Communication.Secs (SECS-II equipment over HSMS-SS)

## Overview

SECS-II messages over an HSMS-SS session for the **equipment** side, for .NET (net472 + net8.0) by Junevy, version `1.0.0-preview.1`. It sits directly on `Junevy.Communication.Core`, `Junevy.Communication.Channels` and `Junevy.Communication.Tcp` — it is **not** an `IPlcClient` and does not reuse the PLC suite. Core principles:

- **Own model, own state**: messages are `SecsMessage` / `SecsItem` (SEMI E5), the session state is `HsmsState` (`NotConnected` / `NotSelected` / `Selected`). The channel's `ConnectionState` is *not* the HSMS state: TCP being connected only means bytes flow.
- **Result-based**: `SendAsync` returns `SecsResult` (`IsSuccess`, `Reply`, `Error`, `ErrorKind`, `RejectReason`, `ErrorMessage`). `StartAsync` returns `CommResult`. Failures on the wire are results; only programming errors throw (`ArgumentException`, `ObjectDisposedException`).
- **`Create` builds, `StartAsync` opens**: `HsmsEquipment.Create(options)` never listens or connects. The options are validated and snapshotted there, so changing the object later does nothing.
- **Stop is not dispose**: after `StopAsync` you may `StartAsync` again; after `Dispose` the equipment is dead (`StartAsync` throws `ObjectDisposedException`, `SendAsync` returns `ConnectionLost`).
- **One dispatcher for both modes**: `Passive` (listen, the host selects) and `Active` (dial the host, send `Select.req` yourself) share handlers, sending, state and results; only the transport differs.
- **Handlers run one at a time**, in arrival order, on the session. A handler may be slow; two handlers never overlap.

## When NOT to Use

- The **host** side (the PC that sends `Select.req` and originates data messages): not implemented in this package.
- SECS-I (RS-232 block transfer), the `J` / `C2` item formats, GEM (planned as the next SECS package) and `Deselect` (HSMS-SS has one session; it is rejected).
- Raw byte protocols over TCP/UDP/serial: `using-junevy-channels`. Address-based PLCs: `using-junevy-plc`. MES reporting: `using-junevy-webapi`.

## Quick Start (Passive equipment)

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs;
using Junevy.Communication.Secs.Hsms;

await using var equipment = HsmsEquipment.Create(new HsmsEquipmentOptions
{
    Host = "0.0.0.0",   // null = every address; IP literal only, host names are not resolved
    Port = 5000,
    DeviceId = 1,
});

// Host sends S1F1 W; answer S1F2 with the equipment identity.
equipment.SetHandler(1, 1, (primary, token) => Task.FromResult<SecsMessage?>(
    primary.CreateReply(SecsItem.L(SecsItem.A("EQ01"), SecsItem.A("1.0.0")))));

CommResult started = await equipment.StartAsync();          // failure = result, not exception
if (!started.IsSuccess)
    throw new InvalidOperationException(started.ErrorMessage);

SecsResult sent = await equipment.SendAsync(
    SecsMessage.Create(5, 1, replyExpected: true, SecsItem.L(SecsItem.B(0x80), SecsItem.U4(1001))));
if (!sent.IsSuccess)
    throw new InvalidOperationException($"{sent.Error}: {sent.ErrorMessage}");
```

- Item factories: `L(...)`, `A(string)`, `B(byte...)`, `Boolean(bool...)`, `I1/I2/I4/I8`, `U1/U2/U4/U8`, `F4`, `F8`. Numeric values are big-endian; `A` is a **single-byte** character (U+0000–U+00FF, Latin-1 on decode) — transcode GBK or UTF-8 yourself and send bytes in a `B` item.
- `primary.CreateReply(body)` builds the matching reply (same stream, function + 1, no W bit). Do not hand-build the function.
- Nothing can be sent before the host selects the session: `SendAsync` returns `NotSelected`. Subscribe to `StateChanged` or poll `State` until `Selected`.

## Active equipment

```csharp
HsmsEquipment.Create(new HsmsEquipmentOptions
{
    Mode = HsmsConnectionMode.Active,
    Host = "192.168.1.10",   // required in Active mode
    Port = 5000,
    DeviceId = 1,
});
```

The equipment dials the host, sends `Select.req` (timeout T6) and keeps reconnecting every T5 after a drop; **every reconnect sends `Select.req` again**, so the session returns to `Selected` on its own. A refused or unanswered `Select` is a failed `StartAsync` result, not an exception.

## Options and Timers

| Property | Default | Meaning |
|---|---|---|
| `Mode` | `Passive` | `Passive` listens; `Active` dials the host and initiates `Select` |
| `Host` | `null` | `Passive`: listen address (`null` = all). `Active`: host address, required. IP literal only |
| `Port` | `5000` | 1–65535 (5000 is the HSMS standard port) |
| `DeviceId` | `0` | HSMS session id, 0–32767; the session id carried by your data messages |
| `T3` | `45000` ms | Reply timeout for a primary with the W bit |
| `T5` | `10000` ms | Connect interval (Active reconnect) |
| `T6` | `5000` ms | Control transaction timeout (`Select`, `Linktest`) |
| `T7` | `10000` ms | Not-selected timeout (Passive waits this long for `Select.req`) |
| `T8` | `5000` ms | Inter-character timeout inside one message |
| `LinktestInterval` | `0` | Keepalive interval; `0` disables `Linktest` |
| `MaxMessageLength` | `16 MiB` | Largest single message (≥ 1024); also the largest frame the channel accepts |

Invalid values throw `ArgumentException` from `Create` (or `factory.GetOrAdd`) naming the property and its range. `HsmsEquipmentOptions` has plain properties and binds straight from `appsettings.json` (`"Mode": "Active"`).

## Handlers

```csharp
equipment.SetHandler(6, 11, async (primary, token) =>
{
    await DoEquipmentWorkAsync(primary.Body, token);
    return primary.CreateReply(SecsItem.B(0x00));    // or null when there is no reply
});
```

- Register **primaries only** (odd function); an even function or stream > 127 throws `ArgumentException`. `RemoveHandler(stream, function)` returns whether it existed.
- A primary with the W bit and no usable reply is answered with `SxF0` (abort) and logged as `Error`; returning a reply of the wrong shape is the same. Without the W bit the return value is ignored (non-null is a `Warning`).
- An exception inside a handler is caught, logged and becomes `SxF0` — it never stops the dispatcher.
- **Honour `token`.** Stopping cancels it and waits for the running handler; the reply of a handler that returns after the cancellation is not sent. A handler may call `StopAsync` itself (that does not deadlock).
- A message with no handler is answered by the package, not by your code: no handler for the stream → S9F3, no handler for that function → S9F5.

## Send Rules and SecsResult

| Situation | `Error` | `ErrorKind` |
|---|---|---|
| Reply received | `None` (`Reply` set) | — |
| Host aborted (`SxF0`) | `Aborted` | `RemoteError` |
| Host sent `Reject.req` | `Rejected` (`RejectReason` = reason code) | `RemoteError` |
| No reply within T3 (S9F9 sent first) | `ReplyTimeout` | `Timeout` |
| Reply not decodable (S9F7 sent first) | `InvalidReply` | `ProtocolViolation` |
| Session not selected | `NotSelected` | `NotConnected` |
| Stopped or disposed | `ConnectionLost` | `NotConnected` |

- Without the W bit the message is only sent; `Reply` is `null`.
- Several sends may be in flight (correlation is by system bytes).
- T3 is a **transaction** timeout, not a link timeout: it produces `ReplyTimeout` and does not close the session.

## S9 System Messages

Sent by the package, never by handlers: S9F1 (wrong session id), S9F3 (no handler for the stream), S9F5 (no handler for that function), S9F7 (body undecodable or over a decode limit), S9F9 (a primary with the W bit timed out). The body is the 10-byte header of the offending message as a binary item.

## SML Text

`ToSml()` renders a message or item as SML for logs and diagnostics (`\n` line endings everywhere, `G9` for `float`, `G17` for `double`, non-printable bytes as `\xHH`). Output only — there is no SML parser.

## Factory and Dependency Injection

```csharp
services.AddSecs();                                    // Junevy.Communication.Secs.DependencyInjection; idempotent
var factory = provider.GetRequiredService<HsmsEquipmentFactory>();
HsmsEquipment line = factory.GetOrAdd("line-1", configuration.GetSection("Secs").Get<HsmsEquipmentOptions>());
```

Same name = same equipment (the second options object is ignored); aliases share the instance; `TryRemove` and disposing the factory dispose the equipment. `GetOrAdd` only builds it — call `StartAsync` yourself. `AddSecs()` registers a singleton; without an `ILoggerFactory` in the container the equipment logs nothing.

## Logging

State changes at `Information`, every frame at `Debug` (direction, stream, function, W bit, system bytes — **no body**), exceptions at `Error`. Bodies can carry recipe data, so SML text stays in `Trace`.

## Not Verified Against Hardware (P51-D18)

`Select.req` uses session id `0xFFFF`; system bytes use the low 16 bits; a handler returning `null` answers `SxF0` (not S9F7); `J` and `C2` are unsupported; a T3 timeout sends S9F9 before returning. These follow the public descriptions of E5 / E37 and are covered by vector tests only — check them on real equipment.

## Common Mistakes

| Mistake | Fix |
|---|---|
| Calling `SendAsync` right after `StartAsync` | Wait for `State == Selected`; otherwise `NotSelected` and nothing is sent |
| Expecting `Create` to listen or connect | `Create` builds only; `StartAsync` opens the session and reports failures as a result |
| Registering a handler for an even function | Handlers are for primaries (odd); the reply path is automatic |
| Not registering a handler and expecting silence | The host gets S9F3 / S9F5 — register `SetHandler(stream, function, ...)` |
| Long blocking work inside a handler | Handlers run one at a time; the next message waits. Hand off to your own queue and honour `token` |
| Ignoring `token` in a handler | Stopping waits for it; the reply is then dropped. Pass the token to every await |
| Treating T3 expiry as a broken link | T3 is a transaction timeout (`ReplyTimeout`); the session stays up |
| Using a host name in `Host` | Only IP literals are accepted; resolve names yourself |
| Changing `HsmsEquipmentOptions` after `Create` | It is a snapshot; rebuild the equipment to change it |
| Calling `StopAsync` and expecting the equipment to be unusable | Stop is restartable; `Dispose` is the permanent end |
| Treating `Selected` as "TCP connected" | `NotSelected` means connected but not selected — `HsmsState` is the HSMS truth |