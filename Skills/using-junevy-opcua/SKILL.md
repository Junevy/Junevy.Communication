---
name: using-junevy-opcua
description: Use when a .NET project references Junevy.Communication.OpcUa or needs OPC UA client access — writing connect, read, write, browse or subscribe code, configuring NodeId text, handling OpcUaValue / OpcUaStatusCode and per-item status codes, tuning reconnect, registering with dependency injection, or troubleshooting NotSupported ("server has no None endpoint"), BadTypeMismatch writes, NotConnected during reconnect, and subscription handlers that block the SDK thread. Client only, .NET 8 only, None security policy and anonymous login only.
---

# using-junevy-opcua

OPC UA client from the Junevy.Communication library: connect, read, write, browse, subscribe, session reconnect. **Client only, .NET 8 only, `None` security policy with anonymous login only.**

## Before anything else

This version connects **only** to servers that offer the `None` security policy **and** allow anonymous logins. A server that has `None` disabled — the default on many real servers — gives `NotSupported`, and no session is ever attempted. Secure channels, client certificates and username logins are not implemented.

## Minimum working code

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa;

await using var client = OpcUaClient.Create(new OpcUaClientOptions
{
    EndpointUrl = "opc.tcp://127.0.0.1:4840/",
    ApplicationName = "line1-plc",
});

CommResult connected = await client.ConnectAsync();
if (!connected.IsSuccess)
{
    Console.WriteLine(connected);   // NotSupported: ... offers no endpoint with the None security policy ...
    return;
}

CommResult<IReadOnlyList<OpcUaValue>> values = await client.ReadAsync(new[] { "ns=2;s=Line1.Speed" });
foreach (OpcUaValue value in values.Data)
    Console.WriteLine($"{value.Value} [{value.Status}]");

CommResult written = await client.WriteAsync("ns=2;s=Line1.Speed", 1234.5);

CommResult<OpcUaSubscription> subscribed = await client.SubscribeAsync(
    new[] { "ns=2;s=Line1.Speed" },
    (_, args) => Console.WriteLine($"{args.NodeId} = {args.Value.Value}"));
```

Options bind straight from `appsettings`:

```csharp
var options = new OpcUaClientOptions();
configuration.GetSection("OpcUa").Bind(options);
using var client = OpcUaClient.Create(options);
```

## The five mistakes that cost the most time

1. **Writing `ns=<index>` in configuration.** A namespace index is only valid for one particular server process. After the server restarts the same variable can land on a different index and the configured node silently points at the wrong thing — or nowhere. Use the namespace **URI**:

   ```csharp
   // Fragile: index 2 of this run only.
   "ns=2;s=Line1.Speed"

   // Stable across restarts; an unknown URI fails the call with InvalidRequest.
   "nsu=urn:myvendor:opcua:line1;s=Line1.Speed"
   ```

2. **Writing a .NET type that does not match the node's data type.** Writes are never converted. An `Int16` node takes a `short`; writing an `int` there is answered by the server with `BadTypeMismatch` **on that item**. A .NET type outside the supported table fails the whole call with `InvalidRequest` before anything is sent.

3. **Treating a per-item Bad status as a failed call.** Read and write return one item per input, in the same order and of the same length; an item status is **data**. A batch containing one unknown node **succeeds** and reports `BadNodeIdUnknown` for that one item:

   ```csharp
   var result = await client.ReadAsync(new[] { "ns=2;s=Ok", "ns=2;s=Gone" });
   if (result.IsSuccess)
       foreach (OpcUaValue item in result.Data)
           if (item.Status.IsBad)   // per node, not per call
               Log($"{item.Status} {item.UnsupportedType}");
   ```

   Only the single-node convenience methods (`ReadAsync(nodeId)`, `WriteAsync(nodeId, value)`) and `BrowseAsync` turn a Bad status into a failed result.

4. **Blocking inside the subscription handler.** Notifications are delivered one by one on the SDK's callback thread and the publish response is held back until the handler returns. A slow handler delays every later notification. Hand the work to a queue and return. A handler that throws is logged as an error and later notifications continue.

5. **Calling read / write / browse / subscribe while the state is not `Connected`.** They return `NotConnected` immediately — they are **not** queued for when the link comes back. During a reconnect this is the normal answer; wait for the state instead of retrying in a loop:

   ```csharp
   client.StateChanged += (_, args) =>
   {
       if (args.Current == OpcUaSessionState.Connected)
           Resubscribe();
   };
   ```

## Node ids

| Form | Example |
|---|---|
| Namespace index | `i=2258`, `ns=2;s=Line1.Speed`, `ns=2;i=1001` |
| Well known | `ns=1;g=<GUID>`, `ns=1;b=<Base64>` |
| Namespace URI | `nsu=urn:myvendor:opcua:line1;s=Speed` |

Browsed node ids come back in `ns=` form and can be read directly. A malformed node id, or an unknown `nsu=` URI, fails the **whole** call with `InvalidRequest` and nothing is sent.

## Value types

`Boolean`, `SByte`, `Byte`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Float`, `Double`, `String`, `DateTime` (UTC), `Guid`, `ByteString` and one-dimensional arrays of those map to the matching BCL types. `LocalizedText`, `StatusCode` and `NodeId` are read-only and come back as `string`, `OpcUaStatusCode` and `string`.

Anything else — structures (`ExtensionObject`), multi-dimensional arrays, heterogeneous `Variant[]`, `QualifiedName` — reads with `Value == null` and `UnsupportedType` naming the type; the status code is still the server's own. Do not treat `Value == null` as "no value": check `Status` and `UnsupportedType`.

`byte[]` is written as a **ByteString**, never as a Byte array; only the node's data type tells the two apart.

## Results, not exceptions

Every operation returns `CommResult` or `CommResult<T>`. Decide on `ErrorKind`; never match `ErrorMessage` text.

| You see | Meaning | What to do |
|---|---|---|
| `NotSupported` | The server offers no `None` endpoint, or no anonymous user token policy | Enable `None` + anonymous on the server, or wait for secure-channel support |
| `NotConnected` | Not `Connected` when the call was made (including while reconnecting) | Connect, or wait for the state |
| `InvalidRequest` | Malformed node id, unknown `nsu=` URI, unsupported .NET type to write, empty or duplicated subscription node ids | Fix the request; nothing was sent |
| `ConnectionClosed` | Refused, reset, dropped, or the client was disposed | Reconnect |
| `Timeout` | `ConnectTimeout` or `OperationTimeout` elapsed | Retry, or raise the timeout |
| `Cancelled` | The caller's token fired | Usually expected |
| `AuthenticationFailed` | Anonymous login rejected by the server | Check the server's user token policies |
| `ResourceExhausted` | Too many sessions / operations / subscriptions / monitored items | Back off or reduce the rate |
| `ProtocolViolation` | Encoding or decoding error | Log the status code; usually a server or version issue |
| `RemoteError` + `ProtocolErrorCode` | Any other Bad status from the server | Log the status code; it is the OPC UA status |

`OpcUaStatusCode` exposes `IsGood` / `IsUncertain` / `IsBad`, `SymbolicName` (`BadNodeIdUnknown`, or `0xXXXXXXXX` for a code the SDK does not know) and `ProtocolErrorCode` on the result carries the code itself.

`Exception` on a failed result is for diagnostics only — never branch on its type.

## Reconnect

An unexpected drop moves the state to `Reconnecting`; a background loop retries with exponential backoff (`ReconnectInterval` doubling up to `MaxReconnectInterval`), unlimited attempts. Operations during that window return `NotConnected` instead of waiting. On success the state becomes `Connected` again and subscriptions are re-established on the server — the same handler keeps receiving notifications and the server resends the current values.

A **first** `ConnectAsync` that fails never starts the loop. `ConnectAsync` while already connected returns success without reconnecting; while `Connecting` or `Reconnecting` it returns `InvalidRequest` instead of queueing. `DisconnectAsync` and disposal stop the loop. `DisconnectAsync` only ends that session: connect again and the next drop is recovered as usual.

A `ConnectAsync` whose token fires (or whose `ConnectTimeout` elapses) returns at once; if the SDK still opens a session afterwards, it is closed immediately and never attached. Disposing a subscription while `Reconnecting` returns at once; it is removed after the session recovers and is not re-established.

## Dependency injection

```csharp
using Junevy.Communication.OpcUa.DependencyInjection;

services.AddOpcUaClients();

OpcUaClientFactory factory = provider.GetRequiredService<OpcUaClientFactory>();
OpcUaClient client = factory.GetOrAdd("line1", options);
```

`AddOpcUaClients()` may be called repeatedly. `GetOrAdd` reuses the instance for a name, so options passed later are ignored — `Create` snapshots the options once.

## API reference

| Member | Notes |
|---|---|
| `OpcUaClient.Create(options, loggerFactory?)` | Validates and snapshots. Throws `ArgumentException` with `ParamName = "options.<Property>"`. |
| `State` / `StateChanged` | Transitions are reported only when the state really changes; `Reason` carries why. |
| `ConnectAsync` | Success if already connected; `InvalidRequest` if a connect or reconnect is in progress; `NotSupported` if the server has no usable endpoint. |
| `DisconnectAsync` | Repeatable; closes the session and ends the reconnect round. |
| `ReadAsync` / `WriteAsync` / `BrowseAsync` | Same length and order as the input; per-item statuses are data. |
| `SubscribeAsync(nodeIds, handler, options?, ct?)` | The handler is a parameter, so the initial value cannot be missed; `sender` is the client. `ItemStatuses` is per item; one rejected item does not fail the call. Invalid options throw `ArgumentException` like `Create`. `ct` covers creation only, not the later `DisposeAsync`. |
| `Dispose` / `DisposeAsync` | Repeatable. Cancels an in-flight connect, stops the reconnect loop, closes the session without waiting for the server's answer. Afterwards `ConnectAsync` throws `ObjectDisposedException`; the other operations return `ConnectionClosed`. |

Full option table, value table, error table and "Verification status" are in
[Junevy.Communication.OpcUa/README.md](file://Junevy.Communication.OpcUa/README.md).

## Behaviour worth knowing

- Only .NET 8. The package does not target net472, because OPC UA 2.0 does not either.
- The SDK drags `Microsoft.Extensions.*` 10.0.12 or newer into your application. Pinning an older one produces NuGet `NU1605`.
- SDK types never appear in this library's public API; everything is a `CommResult`.
- Batches larger than the server's `MaxNodesPerRead` / `MaxNodesPerWrite` are split by the SDK and merged back.
- Values are never logged, at any level. SDK logs are bridged to the same `ILoggerFactory`.
- Verified only against the official server hosted in-process; no real server (KEPServerEX, Prosys, Siemens, Beckhoff) has been used yet.