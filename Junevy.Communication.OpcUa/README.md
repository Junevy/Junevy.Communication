# Junevy.Communication.OpcUa

OPC UA client for the [Junevy.Communication](https://github.com/Junevy/Junevy.Communication) communication library.

> **This version only connects to servers that offer the `None` security policy and allow anonymous logins.**
> Many real servers disable `None` by default; connecting to those returns `NotSupported` without any
> request being sent. Secure channels (Sign / SignAndEncrypt), client certificates and username logins are
> not implemented yet.

- **Client only.** OPC UA servers are not part of this package.
- **.NET 8 only.** Unlike the other packages of this repository, this one does not target .NET Framework 4.7.2, because the OPC UA 2.0 line no longer does.

The OPC UA protocol itself — the secure channel, sessions, the binary encoding, browsing and subscriptions — is handled by the OPC Foundation's .NET Standard Stack ([MIT](https://github.com/OPCFoundation/UA-.NETStandard)). This package is a thin adapter around it: it exposes its own types, keeps the SDK out of the public API, turns every SDK failure into a `CommResult`, and maps the SDK's connection states onto `OpcUaSessionState`.

## Dependency footprint

`OPCFoundation.NetStandard.Opc.Ua.Client` 2.0.1 pulls `Microsoft.Extensions.*` **10.0.12 or newer** (logging, options, dependency injection, Http, telemetry) into your application, plus `System.Text.Json` 10.0.12, `System.IO.Pipelines` 10.0.12, `System.Threading.RateLimiting` 10.0.12, `Polly` 8.4.2 and `BitFaster.Caching` 2.6.1. This is a consequence of the SDK, not of this package. If your application pins an older `Microsoft.Extensions.*`, raise it to the SDK's version; otherwise NuGet reports `NU1605` (package downgrade).

## Getting started

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa;

await using OpcUaClient client = OpcUaClient.Create(new OpcUaClientOptions
{
    EndpointUrl = "opc.tcp://127.0.0.1:4840/",
    ApplicationName = "line1-plc",
});

CommResult connected = await client.ConnectAsync();
if (!connected.IsSuccess)
{
    Console.WriteLine(connected);   // e.g. "NotSupported: The server at ... offers no endpoint with the None security policy ..."
    return;
}

CommResult<IReadOnlyList<OpcUaValue>> values = await client.ReadAsync(new[] { "nsu=urn:junevy:opcua:test;s=Line1.Speed" });
CommResult written = await client.WriteAsync("ns=2;s=Line1.Speed", 1234.5);
CommResult<IReadOnlyList<OpcUaReference>> children = await client.BrowseAsync("i=85");

CommResult<OpcUaSubscription> subscribed = await client.SubscribeAsync(
    new[] { "ns=2;s=Line1.Speed" },
    (_, args) => Console.WriteLine($"{args.NodeId} = {args.Value.Value}"));
```

Single node convenience overloads return one `CommResult` instead of a list: `await client.ReadAsync("ns=2;s=Line1.Speed")` and `await client.WriteAsync("ns=2;s=Line1.Speed", 1234.5)`. A Bad item status becomes a failure there, carrying the status code in `ProtocolErrorCode`; `Uncertain` still counts as success.

`OpcUaClientOptions` is a plain POCO of simple properties, so it binds straight from `appsettings`:

```csharp
var options = new OpcUaClientOptions();
configuration.GetSection("OpcUa").Bind(options);
OpcUaClient client = OpcUaClient.Create(options);
```

`Create` validates the options once and takes a **snapshot**: changing the options object afterwards does not affect the client.

## Node ids

Node ids are plain OPC UA text; no SDK type is ever exposed.

| Form | Example |
|---|---|
| Namespace index | `i=2258`, `ns=2;s=Line1.Speed`, `ns=2;i=1001` |
| Well known | `ns=1;g=<GUID>`, `ns=1;b=<Base64>` |
| Namespace **URI** | `nsu=urn:junevy:opcua:test;s=Counter` |

**Prefer `nsu=` in configuration.** A namespace index is only valid for one particular server process — after the server restarts, the same variable can land on a different index, and the configured node id silently points at the wrong thing or nowhere. A namespace URI survives that.

A malformed node id, or a `nsu=` URI the server does not know, fails the **whole** call with `InvalidRequest` before anything is sent. Browsed node ids are returned in the `ns=` form and can be read directly.

## Value types

| OPC UA built-in type | Read as | Written from |
|---|---|---|
| Boolean | `bool` | `bool` |
| SByte / Byte | `sbyte` / `byte` | same |
| Int16 / UInt16 | `short` / `ushort` | same |
| Int32 / UInt32 | `int` / `uint` | same |
| Int64 / UInt64 | `long` / `ulong` | same |
| Float / Double | `float` / `double` | same |
| String | `string` | `string` |
| DateTime | `DateTime` (`Kind = Utc`) | `DateTime` (`Local` is converted to UTC, `Unspecified` is taken as UTC) |
| Guid | `Guid` | `Guid` |
| ByteString | `byte[]` | `byte[]` |
| LocalizedText | `string` (text only) | — |
| StatusCode | `OpcUaStatusCode` | — |
| NodeId | `string` (`ns=` form) | — |
| One-dimensional array of the nine scalar types above | the matching array (`int[]`, `string[]`, …) | same |
| Anything else (ExtensionObject / structures, multi-dimensional arrays, heterogeneous Variant arrays, QualifiedName, …) | unsupported: `Value` is `null`, `UnsupportedType` names the type | `InvalidRequest` |

`byte[]` is written as a **ByteString**, never as a Byte array. The two are only told apart by the node's data type — a `byte[]` written to a Byte array node is a Byte array, and written to a ByteString node is a ByteString.

**Writes are never converted.** The .NET type must match the node's data type exactly: an `Int16` node takes a `short`, writing an `int` there is answered by the server with `BadTypeMismatch` on that item. A .NET type outside the "written from" column fails the whole call with `InvalidRequest` before anything is sent, because such a value cannot be encoded at all — continuing would leave the batch half written.

## Batch semantics

Read and write return **one item per input, in the same order and of the same length**, each with its own status code.

- An item status is **data**, not a failure: a batch containing an unknown node succeeds and reports `BadNodeIdUnknown` for that one item.
- Only a service-level failure — a dropped session, a timeout, a server that rejects the request itself — fails the whole call.
- An empty list succeeds immediately and sends nothing.
- Requests larger than the server's `MaxNodesPerRead` / `MaxNodesPerWrite` are split by the SDK and merged back, so one call may become several requests. There is no second batching implementation in this package.

## Browsing

```csharp
CommResult<IReadOnlyList<OpcUaReference>> children = await client.BrowseAsync("i=85");
```

- Forward, hierarchical references only, including subtypes, all node classes.
- Continuation points are followed to the end: the list is complete.
- A Bad status for the node itself (`BadNodeIdUnknown`, …) fails the call, classified from the status code.

## Subscriptions

```csharp
CommResult<OpcUaSubscription> subscribed = await client.SubscribeAsync(
    new[] { "ns=2;s=Line1.Speed", "ns=2;s=Line1.State" },
    (_, args) => Console.WriteLine($"{args.NodeId} = {args.Value.Value}"),
    new OpcUaSubscriptionOptions { PublishingInterval = 500, QueueSize = 10 });

OpcUaSubscription subscription = subscribed.Data;
await subscription.DisposeAsync();
```

- Every call creates one OPC UA subscription with one monitored item per node id, on the `Value` attribute, in reporting mode.
- The handler is a **parameter**, not an event: it is already attached when `SubscribeAsync` returns, so the initial value cannot be missed.
- `SubscribeAsync` returns once the server has created the subscription and every monitored item has been created. `ItemStatuses` is in the same order as `NodeIds`; an item the server rejected is `Bad` there and the **call still succeeds** — the other items keep notifying.
- Notifications are delivered one by one on the SDK's callback thread, with the client as `sender`. **The handler must not block**: the publish response is held back until it returns, so a slow handler delays later notifications. Each subscriber of a multicast handler is called separately: one that throws is logged as an error, and the other subscribers and later notifications continue.
- The cancellation token only covers creating the subscription. Creation is bounded by `OperationTimeout`; when it times out or is cancelled, the call fails (`Timeout` / `Cancelled`) and nothing is left behind, neither on the server nor in the session.
- `DisposeAsync` deletes the subscription on the server, best effort, and may be called more than once. While the session is `Reconnecting` it returns at once and the subscription is removed as soon as the session has recovered. After a disconnect or after the client is gone it does nothing.
- An empty list, or the same node id twice, returns `InvalidRequest`.
- After a reconnect the subscription is re-established on the server and the same handler keeps receiving notifications; the server resends the current values. Disposed subscriptions are not re-established.

## Reconnect

1. On an unexpected drop the state becomes `Reconnecting`.
2. A background loop reconnects with exponential backoff (`ReconnectInterval` doubling up to `MaxReconnectInterval`, 20 % jitter, unlimited attempts).
3. While the state is not `Connected`, read, write, browse and subscribe all return `NotConnected` immediately — they are **not** queued and sent once the link is back.
4. `DisconnectAsync` and disposal stop the loop and do not wait for a backoff to expire.

A **first** `ConnectAsync` that fails never starts the loop. `ConnectAsync` while already connected returns success without reconnecting; while `Connecting` or `Reconnecting` it returns `InvalidRequest` instead of queueing behind the running attempt.

The SDK ignores cancellation while it discovers endpoints, so `ConnectAsync` stops waiting by itself: when the caller's token fires or `ConnectTimeout` elapses it returns at once, and a session the SDK still manages to open afterwards is closed straight away — it is never attached to the client and never left on the server.

`DisconnectAsync` ends the current session only. Connect again later, and the next unexpected drop is recovered as usual.

## State and events

```csharp
client.StateChanged += (_, args) => Log(args.Previous, args.Current, args.Reason);
OpcUaSessionState state = client.State;
```

`OpcUaSessionState` is `Disconnected`, `Connecting`, `Connected` or `Reconnecting`. Transitions are reported in the order they happen, and **only when the state really changes** — the SDK raises its event on every reconnect attempt, which is merged here. A subscriber that throws is logged as an error and does not affect the state or the other subscribers.

## Options

| Property | Default | Meaning |
|---|---|---|
| `EndpointUrl` | — | Server endpoint, `opc.tcp://host:port[/path]`. **Required.** |
| `ApplicationName` | `Junevy.Communication.OpcUa` | Application name reported to the server. |
| `ApplicationUri` | `urn:{machine}:{ApplicationName}` | Application URI; `null` derives it. |
| `PkiRoot` | `%LocalAppData%\Junevy.Communication\OpcUa\pki` | Certificate store the SDK configuration needs. No client certificate is generated under the `None` policy. |
| `ConnectTimeout` | `10000` | Milliseconds for endpoint discovery **and** session creation. |
| `OperationTimeout` | `10000` | Milliseconds per service call. |
| `SessionTimeout` | `60000` | Session timeout requested from the server. |
| `KeepAliveInterval` | `5000` | Keep-alive interval; this is how a dead link is noticed. |
| `ReconnectInterval` | `1000` | Milliseconds before the first reconnect attempt. |
| `MaxReconnectInterval` | `30000` | Backoff ceiling in milliseconds; may not be below `ReconnectInterval`. |

Subscription options (`OpcUaSubscriptionOptions`): `PublishingInterval` 1000 ms, `SamplingInterval` `null` (= the publishing interval, `0` means the server's fastest), `QueueSize` 1, `DiscardOldest` `true`.

Invalid options are rejected by `Create` with an `ArgumentException` whose `ParamName` is `options.<Property>` and whose message states the allowed range. Invalid subscription options are rejected the same way by `SubscribeAsync`.

## Disposal

`Dispose` and `DisposeAsync` are repeatable. They cancel a connect that is still in progress (that `ConnectAsync` returns `ConnectionClosed`), stop the reconnect loop, close the session and release the SDK. Disposal does not wait for the server to acknowledge the close, so it returns promptly even while `Reconnecting`. Afterwards `ConnectAsync` throws `ObjectDisposedException`, while read, write, browse, subscribe and disconnect return `ConnectionClosed`.

## Error classification

Failures come back as `CommResult` with an `ErrorKind` and, where the server supplied one, a `ProtocolErrorCode` holding the OPC UA status code.

| Situation | `ErrorKind` | `ProtocolErrorCode` |
|---|---|---|
| Caller's cancellation token fires (judged from the token, not from the SDK's exception) | `Cancelled` | — |
| Connect timeout, or a service call exceeds `OperationTimeout` | `Timeout` | — |
| BadTimeout, BadRequestTimeout | `Timeout` | status code |
| BadNotConnected, BadConnectionClosed, BadSecureChannelClosed, BadSessionClosed, BadSessionIdInvalid, BadServerNotConnected, BadCommunicationError | `ConnectionClosed` | status code |
| BadIdentityTokenInvalid, BadIdentityTokenRejected, BadUserAccessDenied, BadSecurityChecksFailed, BadCertificate* | `AuthenticationFailed` | status code |
| BadTooManySessions, BadTooManyOperations, BadTooManySubscriptions, BadTooManyMonitoredItems, BadOutOfMemory, BadResourceUnavailable | `ResourceExhausted` | status code |
| BadServiceUnsupported, BadNotSupported, BadSecurityPolicyRejected, BadSecurityModeRejected | `NotSupported` | status code |
| BadEncodingError, BadDecodingError, BadEncodingLimitsExceeded | `ProtocolViolation` | status code |
| Any other Bad status | `RemoteError` | status code |
| Connection refused, socket error | `ConnectionClosed` | — |
| Read, write, browse or subscribe while not `Connected`, or racing a `DisconnectAsync` that closes the session as the call starts | `NotConnected` | — |
| The session is closed by a concurrent disconnect or disposal while the call is on the wire | `ConnectionClosed` | — |
| The server offers no `None` endpoint, or no anonymous user token policy | `NotSupported` | — |
| Malformed node id, unknown `nsu=` URI, unsupported .NET type to write, empty or duplicated subscription node ids | `InvalidRequest` | — |
| Any operation after disposal | `ConnectionClosed` | — |
| Any other SDK failure | `Unspecified` | — |

Per-item statuses of a read, a write or a subscription are **data**: only the single-node convenience methods and `BrowseAsync` turn a Bad status into a failure.

`OpcUaStatusCode` judges Good / Uncertain / Bad from the top two bits itself, following OPC UA Part 4, rather than reusing the SDK's predicates (the SDK reports both `IsUncertain` and `IsBad` for `0xC0000000`). `SymbolicName` is the specification's name, or `0xXXXXXXXX` when the code is not one the SDK knows.

Exceptions never escape the public methods; they are attached to `CommResult.Exception` for diagnostics only.

## Dependency injection

```csharp
using Junevy.Communication.OpcUa.DependencyInjection;

services.AddOpcUaClients();

OpcUaClientFactory factory = provider.GetRequiredService<OpcUaClientFactory>();
OpcUaClient client = factory.GetOrAdd("line1", options);
```

`AddOpcUaClients()` may be called repeatedly. Disposing the factory disposes the clients it created.

## Logging

Pass an `ILoggerFactory` to `Create` (or let the container supply one) to get logs. State changes are logged at Information, each operation at Debug (node count and duration) and failures at Warning. **Values are never logged, at any level.** SDK logs are bridged to the same factory through the SDK's telemetry context.

## Verification status

1. Verified only against the official OPC Foundation server hosted in-process. No real server — KEPServerEX, Prosys, Siemens, Beckhoff — has been used yet.
2. Only the `None` security policy with anonymous login is supported. Many real servers disable `None` by default, so a connection to them returns `NotSupported` before any request is sent.
3. Built against OPC Foundation 2.0.1, released 2026-10; that version line is new and thinly documented.
4. Writes are not converted: the .NET type must match the node's data type.

## License

MIT