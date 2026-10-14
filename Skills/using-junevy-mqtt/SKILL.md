---
name: using-junevy-mqtt
description: Use when a .NET project references Junevy.Communication.Mqtt or needs MQTT 3.1.1 / 5.0 messaging — writing connect, publish, subscribe or receive code, configuring the last will, tuning reconnect and subscription restore, registering with dependency injection, or handling MqttClient results, CommErrorKind and ProtocolErrorCode. Also covers debugging disconnects, late acknowledgements, timeouts, or treating ConnectionClosed as "the message arrived".
---

# using-junevy-mqtt

MQTT client from the Junevy.Communication library: MQTT 3.1.1 and 5.0, TLS, MQTT 5 properties, last will and testament, session-resuming reconnect. **Client only, .NET 8 only.**

## Minimum working code

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt;

using var client = MqttClient.Create(new MqttClientOptions
{
    Host = "broker.local",
    Port = 1883,
    ClientId = "line1-plc",
    ProtocolVersion = MqttProtocolVersion.V500,
});

client.MessageReceived += (_, args) =>
    Console.WriteLine($"{args.Message.Topic}: {args.Message.GetPayloadAsString()}");

CommResult connected = await client.ConnectAsync();
if (!connected.IsSuccess)
{
    Console.WriteLine(connected);   // AuthenticationFailed: ... (code 0x87)
    return;
}

await client.SubscribeAsync("plant/+/temp", MqttQos.AtLeastOnce);
await client.PublishAsync(new MqttMessage("plant/line1/temp", "21.5") { Qos = MqttQos.AtLeastOnce });
```

Options bind straight from `appsettings`:

```csharp
var options = new MqttClientOptions();
configuration.GetSection("Mqtt").Bind(options);   // enums bind by name: "ProtocolVersion": "V500"
using var client = MqttClient.Create(options);
```

## The five mistakes that cost the most time

1. **Subscribing before `ConnectAsync` succeeded.** The client starts in `Disconnected`; subscribe and publish return `NotConnected` until the connection is up. Subscribe after connecting, or on the `Connected` state.

   ```csharp
   client.StateChanged += (_, args) =>
   {
       if (args.Current == MqttConnectionState.Connected)
           _ = client.SubscribeAsync("plant/#", MqttQos.AtLeastOnce);
   };
   ```

2. **Blocking inside `MessageReceived`.** The event runs synchronously on the SDK's receive thread and the QoS acknowledgement is only sent after **every** subscriber returns. A slow subscriber holds back later messages and their acknowledgements on the same connection. Hand the work to a queue or a background task and return.

3. **Using MQTT 5 properties on a 3.1.1 connection.** `ContentType`, `ResponseTopic`, `CorrelationData`, `MessageExpiryInterval` and `UserProperties` return `InvalidRequest` on `MqttProtocolVersion.V311`, and nothing is sent — the library never silently drops them. Set `ProtocolVersion = MqttProtocolVersion.V500` if you need them.

4. **Treating `ConnectionClosed` from a publish as "the broker got it".** When a publish fails with `ConnectionClosed`, whether the broker already received the message is unknown and the library does **not** resend. There is no offline queue. Decide at the application level: retry with an idempotent key, persist to a local outbox, or accept the loss.

5. **Expecting the last will after a normal disconnect.** The broker publishes the will only on an **abnormal** disconnect. `DisconnectAsync` and disposal send a DISCONNECT, so the broker discards the will. Also note the will is carried on **every** connection, including reconnects — that is what you want for a liveness signal.

   ```csharp
   var options = new MqttClientOptions
   {
       Host = "broker.local",
       ClientId = "line1-plc",
       WillTopic = "devices/line1-plc/status",
       WillPayload = "offline",
       WillRetain = true,
   };
   // The host publishes the matching retained "online" message itself after connecting;
   // this library never publishes it for you.
   ```

## Reconnect and subscription restore

`AutoReconnect` defaults to **true** (MQTT session recovery is protocol-native). After an unexpected drop:

- the state becomes `Reconnecting`;
- a background loop retries with exponential backoff (`ReconnectInterval` doubling up to `MaxReconnectInterval`), unlimited attempts, including after authentication failures;
- if the broker reports **Session Present = false**, every remembered filter is re-subscribed one by one. A filter the **broker rejects** is logged and forgotten; a timeout or a drop **while restoring** fails that attempt instead, the table is kept and the loop tries again;
- the state becomes `Connected` **after** the subscriptions are back, so subscribing on `Connected` is safe.

A **first** `ConnectAsync` that fails never starts the loop. With `AutoReconnect = false` an unexpected drop goes straight to `Disconnected`. `DisconnectAsync` ends the current reconnect round only: after a new `ConnectAsync`, the next unexpected drop reconnects again.

## Results, not exceptions

Every operation returns `CommResult` or `CommResult<MqttQos>`. Decide on `ErrorKind`; never match `ErrorMessage` text.

| You see | Meaning | What to do |
|---|---|---|
| `NotConnected` | Not connected when the call was made | Connect first |
| `InvalidRequest` | Bad topic / filter, MQTT 5 property on 3.1.1, undefined QoS | Fix the request; nothing was sent |
| `AuthenticationFailed` | Bad credentials, or a TLS certificate problem | Check credentials / certificate |
| `Timeout` | Connect or acknowledgement wait exceeded `Timeout` | Retry, or raise `Timeout` |
| `Cancelled` | The caller's token fired (also while `ConnectAsync` waited behind another lifecycle operation) | Usually expected |
| `ConnectionClosed` | Refused, reset, or dropped mid-operation | Reconnect; for publishes see mistake 4 |
| `RemoteError` + `ProtocolErrorCode` | The broker rejected the packet with a reason code | Log the reason code; it is the MQTT reason |
| `ResourceExhausted` | Broker-side quota / rate limit | Back off |

`Exception` on a failed result is for diagnostics only — never branch on its type.

## Dependency injection

```csharp
using Junevy.Communication.Mqtt.DependencyInjection;

services.AddMqttClients();

MqttClientFactory factory = provider.GetRequiredService<MqttClientFactory>();
MqttClient client = factory.GetOrAdd("line1", options);
```

`AddMqttClients()` may be called repeatedly. `GetOrAdd` reuses the instance for a name, so options passed later are ignored — `Create` snapshots the options once.

## API reference

| Member | Notes |
|---|---|
| `MqttClient.Create(options, loggerFactory?)` | Validates and snapshots. Throws `ArgumentException` with `ParamName = "options.<Property>"`. |
| `State` / `StateChanged` | Transitions are reported only when the state really changes; `Reason` carries why. |
| `MessageReceived` | Synchronous on the SDK thread. Do not block. |
| `ConnectAsync` | Success if already connected; `InvalidRequest` if a connect or reconnect is in progress. |
| `DisconnectAsync` | Repeatable; sends DISCONNECT and ends the current reconnect round. No result: failures are logged. |
| `PublishAsync` / `SubscribeAsync` / `UnsubscribeAsync` | `SubscribeAsync` returns the QoS the **broker granted**. |
| `Dispose` / `DisposeAsync` | Repeatable. Cancels an in-flight connect, stops the reconnect loop and waits for both. Afterwards publish, subscribe and unsubscribe return `ConnectionClosed`; `ConnectAsync` and `DisconnectAsync` throw `ObjectDisposedException`. |

Full option table, error table and "Verification status" are in
[Junevy.Communication.Mqtt/README.md](file://Junevy.Communication.Mqtt/README.md).

## Behaviour worth knowing

- Only .NET 8. The package does not target net472, because MQTTnet 5 does not either.
- SDK types never appear in this library's public API; everything is a `CommResult`.
- Passwords and payloads are never logged, at any level. SDK logs are bridged at Warning and above under the category `Junevy.Communication.Mqtt.Sdk`.
- Verified only against the in-process MQTTnet server; no real broker (Mosquitto, EMQX, HiveMQ) has been used yet.