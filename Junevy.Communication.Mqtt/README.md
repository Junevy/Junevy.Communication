# Junevy.Communication.Mqtt

MQTT client for the [Junevy.Communication](https://github.com/Junevy/Junevy.Communication) communication library.

- **Client only.** Brokers are not part of this package.
- **MQTT 3.1.1 and 5.0.** MQTT 3.1 is not supported.
- **.NET 8 only.** Unlike the other packages of this repository, this one does not target .NET Framework 4.7.2, because MQTTnet 5 itself no longer does.

Everything above .NET 8 — TLS, MQTT 5 properties, last will and testament, session-resuming reconnect — is built on [MQTTnet](https://github.com/dotnet/MQTTnet) (MIT). This package is a thin adapter around it: it exposes its own types, keeps the SDK out of the public API, and translates every SDK failure into `CommResult`.

## Getting started

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt;

MqttClient client = MqttClient.Create(new MqttClientOptions
{
    Host = "broker.local",
    Port = 1883,
    ClientId = "line1-plc",
    ProtocolVersion = MqttProtocolVersion.V500,
});

client.MessageReceived += (_, args) => Console.WriteLine($"{args.Message.Topic}: {args.Message.GetPayloadAsString()}");

CommResult connected = await client.ConnectAsync();
if (!connected.IsSuccess)
{
    Console.WriteLine(connected);   // e.g. "AuthenticationFailed: The broker rejected the connection: NotAuthorized. (code 0x87)"
    return;
}

await client.SubscribeAsync("plant/+/temp", MqttQos.AtLeastOnce);

CommResult published = await client.PublishAsync(new MqttMessage("plant/line1/temp", "21.5")
{
    Qos = MqttQos.AtLeastOnce,
    Retain = true,
    ContentType = "text/plain",
});
```

`MqttClientOptions` is a plain POCO of simple properties, so it binds straight from `appsettings`:

```csharp
var options = new MqttClientOptions();
configuration.GetSection("Mqtt").Bind(options);   // enums bind by name, e.g. "ProtocolVersion": "V500"
using MqttClient client = MqttClient.Create(options);
```

`Create` validates the options once and takes a **snapshot**: changing the options object afterwards does not affect the client.

## Options

| Property | Default | Meaning |
|---|---|---|
| `Host` | — | Broker host name or IP. **Required.** |
| `Port` | `1883` | Broker port; usually `8883` with TLS. |
| `ClientId` | — | Client identifier. **Required**, and must be unique on that broker. |
| `UserName` | null | Required when `Password` is set (MQTT 3.1.1 rule). |
| `Password` | null | Never written to the log, at any level. |
| `ProtocolVersion` | `V311` | `V311` or `V500`. |
| `CleanSession` | `true` | Clean Session (3.1.1) / Clean Start (5.0). `false` asks the broker to keep the session. |
| `SessionExpiryInterval` | `0` | Seconds; may only be non-zero with `V500`. |
| `KeepAliveSeconds` | `15` | 0–65535; `0` means no PINGREQ. |
| `Timeout` | `10000` | Milliseconds; the connect timeout **and** the wait for every acknowledged packet. |
| `UseTls` | `false` | TLS. |
| `AllowUntrustedCertificates` | `false` | Accepts any server certificate, including a hostname mismatch. `Create` logs a warning when this is on. |
| `AutoReconnect` | `true` | Reconnect after an unexpected drop and restore the subscriptions. |
| `ReconnectInterval` | `1000` | Milliseconds before the first reconnect; then doubles up to `MaxReconnectInterval`. |
| `MaxReconnectInterval` | `30000` | Backoff ceiling in milliseconds. |
| `WillTopic` | null | Last will topic; `null` means no will. |
| `WillPayload` | null | Last will payload, UTF-8 text. |
| `WillQos` | `AtMostOnce` | Last will QoS. |
| `WillRetain` | `false` | Whether the last will is retained. |

Invalid options are rejected by `Create` with an `ArgumentException` whose `ParamName` is `options.<Property>` and whose message states the allowed range.

## State and events

```csharp
client.StateChanged += (_, args) => Log(args.Previous, args.Current, args.Reason);
MqttConnectionState state = client.State;
```

`MqttConnectionState` is `Disconnected`, `Connecting`, `Connected` or `Reconnecting`. Transitions are reported in the order they happen, and **only when the state really changes**. A subscriber that throws is logged as an error and does not affect the state or the other subscribers.

`Connected` is reported **after** the subscriptions have been restored (see below), so a host that subscribes on `Connected` sees a fully usable client.

## Publishing and subscribing

```csharp
CommResult<MqttQos> granted = await client.SubscribeAsync("plant/#", MqttQos.AtLeastOnce);
CommResult unsubscribed = await client.UnsubscribeAsync("plant/#");
```

- QoS 0 succeeds as soon as the packet is written; QoS 1 waits for the PUBACK; QoS 2 waits for the PUBCOMP.
- `SubscribeAsync` returns the QoS **the broker granted**, which may be lower than requested.
- An empty topic, or one containing `+` or `#`, returns `InvalidRequest` and nothing is sent. Filter validity is decided by the SDK's own validator.
- While the client is not `Connected`, publish, subscribe and unsubscribe all return `NotConnected`.
- A publish interrupted by a disconnect returns `ConnectionClosed` and is **not** resent: whether the broker already received it is unknown, and that is the host's decision. There is no offline queue.

## Receiving

`MessageReceived` is raised synchronously on the SDK's receive thread. **Subscribers must not block** — the QoS acknowledgement is only sent after every subscriber has returned, so a slow subscriber holds back later messages on the same connection.

The payload is copied out of the SDK's buffer before the event is raised, so the `MqttMessage` is safe to keep.

## Reconnect and subscription restore

When the SDK reports a disconnect that was not asked for by the host, and `AutoReconnect` is on:

1. The state becomes `Reconnecting`.
2. A background loop reconnects with exponential backoff (`ReconnectInterval` doubling up to `MaxReconnectInterval`, unlimited attempts). Authentication failures keep retrying, because the credentials on the broker side may since have been corrected.
3. On success: if the broker reports **Session Present = false**, every filter in the subscription table is re-subscribed, one by one; a filter the broker rejects is logged as a warning and forgotten. Then, and only then, the state becomes `Connected`.
4. `DisconnectAsync` and disposal stop the loop **and wait for it to exit**, so no background task survives the client.

`AutoReconnect = false` turns an unexpected drop straight into `Disconnected` with a non-empty `Reason`. A **first** `ConnectAsync` that fails never starts the loop.

## TLS

```csharp
new MqttClientOptions { Host = "broker.local", Port = 8883, ClientId = "line1", UseTls = true }
```

An untrusted certificate or a hostname mismatch is reported as `AuthenticationFailed`, following the same classification the TCP package uses. `AllowUntrustedCertificates = true` accepts anything and logs a warning at `Create`.

## MQTT 5 properties

`ContentType`, `ResponseTopic`, `CorrelationData`, `MessageExpiryInterval` and `UserProperties` are set with `init`:

```csharp
var message = new MqttMessage("devices/1/command", "restart")
{
    Qos = MqttQos.AtLeastOnce,
    ContentType = "application/json",
    ResponseTopic = "devices/1/command/reply",
    CorrelationData = requestId,
    MessageExpiryInterval = 30,
    UserProperties = new[] { new MqttUserProperty("site", "shanghai") },
};
```

Publishing any of them on a 3.1.1 connection returns `InvalidRequest` and **nothing is sent** — the properties are never silently dropped. The same properties are populated on messages that arrive.

## Last will and testament

```csharp
new MqttClientOptions
{
    Host = "broker.local",
    ClientId = "line1-plc",
    WillTopic = "devices/line1-plc/status",
    WillPayload = "offline",
    WillRetain = true,
}
```

The broker publishes the will only on an **abnormal** disconnect, and the will is carried on **every** connection including reconnects. `DisconnectAsync` and disposal send a DISCONNECT, so per the MQTT specification the broker discards the will.

The usual pattern is a retained `offline` will plus an `online` message that the host publishes itself after connecting — this library does not publish the online message for you.

## Error classification

Failures come back as `CommResult` with an `ErrorKind` and, where the broker supplied one, a `ProtocolErrorCode` holding the MQTT reason code.

| Situation | `ErrorKind` | `ProtocolErrorCode` |
|---|---|---|
| Caller's cancellation token fires | `Cancelled` | — |
| Connect timeout, or a wait for an acknowledgement times out | `Timeout` | — |
| CONNACK rejected: BadUserNameOrPassword, NotAuthorized, Banned, BadAuthenticationMethod | `AuthenticationFailed` | reason code |
| CONNACK rejected: ServerBusy, QuotaExceeded, ConnectionRateExceeded | `ResourceExhausted` | reason code |
| CONNACK rejected: UnsupportedProtocolVersion | `NotSupported` | reason code |
| Any other CONNACK failure | `RemoteError` | reason code |
| TLS authentication failed (untrusted certificate, hostname mismatch) | `AuthenticationFailed` | — |
| Connection refused, socket error, disconnect during an operation | `ConnectionClosed` | — |
| Publish, subscribe or unsubscribe while not connected | `NotConnected` | — |
| PUBACK / PUBREC reason code ≥ 0x80: QuotaExceeded | `ResourceExhausted` | reason code |
| Other PUBACK / PUBREC reason codes ≥ 0x80 | `RemoteError` | reason code |
| SUBACK reason code ≥ 0x80 | `RemoteError` | reason code |
| UNSUBACK reason code ≥ 0x80 | `RemoteError` | reason code |
| Invalid topic or filter, MQTT 5 property on a 3.1.1 connection, undefined QoS | `InvalidRequest` | — |
| Publish, subscribe or unsubscribe after disposal | `ConnectionClosed` | — |
| Any other SDK failure | `Unspecified` | — |

Reason codes below 0x80 are acknowledgements, so they count as success — MQTT 5 PUBACK `0x10 "No matching subscribers"` for instance.

Exceptions never escape the public methods; they are attached to `CommResult.Exception` for diagnostics only.

## Dependency injection

```csharp
using Junevy.Communication.Mqtt.DependencyInjection;

services.AddMqttClients();

MqttClientFactory factory = provider.GetRequiredService<MqttClientFactory>();
MqttClient client = factory.GetOrAdd("line1", options);
```

`AddMqttClients()` may be called repeatedly. Disposing the factory disposes the clients it created.

## Logging

Pass an `ILoggerFactory` to `Create` to get logs. State changes are logged at Information, each operation at Debug (topic, size and duration) and failures at Warning. **Passwords and payloads are never logged, at any level.** SDK logs are bridged to the same factory under the category `Junevy.Communication.Mqtt.Sdk`, at Warning and above only.

## Verification status

1. Verified only against the in-process MQTTnet server. No real broker — Mosquitto, EMQX, HiveMQ — has been used yet.
2. When the broker reports Session Present, no subscriptions are re-sent; this relies on the broker having kept the session correctly.
3. A blocking `MessageReceived` subscriber delays subsequent messages and their acknowledgements on the same connection.

## License

MIT