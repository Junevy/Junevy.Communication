# Junevy.Communication.Mqtt

MQTT client for the [Junevy.Communication](https://github.com/Junevy/Junevy.Communication) communication library.

## Scope

- **Client only.** Brokers are not part of this package.
- **MQTT 3.1.1 and 5.0** (`MqttProtocolVersion.V311` and `V500`). MQTT 3.1 is not supported.
- **.NET 8 only.** Unlike the other packages of this repository, this one does not target .NET Framework 4.7.2, because MQTTnet 5 itself no longer does.
- Everything above .NET 8 — TLS, MQTT 5 properties, last will and testament, session-resuming reconnect — is built on [MQTTnet](https://github.com/dotnet/MQTTnet) (MIT). This package is a thin adapter around it: it exposes its own types and keeps the SDK out of the public API.

## Dependencies

- `Junevy.Communication.Core` (result types, backoff, event fan-out, `NamedFactory`)
- `MQTTnet` 5.2.0.1603 (MIT)

## Usage

See `Skills/using-junevy-mqtt/SKILL.md` for working examples, or the [package documentation](https://github.com/Junevy/Junevy.Communication) for the full option, error and reconnection reference.

## License

MIT