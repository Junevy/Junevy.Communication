# Junevy.Communication.Core

Transport-independent building blocks shared by the communication libraries: result types with a machine-readable error classification, backoff policies, a timeout scope that separates timeouts from user cancellation, a hex formatter for diagnostics, and a named registry with aliases.

Core does not depend on `System.IO.Pipelines` or `System.Threading.Channels`. Protocol packages that do not use byte channels can reference it on its own.

> Preview (`1.0.0-preview.1`): the public API may change before the P2 milestone (PLC protocol suite and MELSEC validation). See the root `readme.md`.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

## Installation

```text
dotnet add package Junevy.Communication.Core --prerelease
```

## Results and Error Kinds

`CommResult` (no data) and `CommResult<T>` (with data) are immutable and can only be created through `Success` and `Fail`. Decide on `ErrorKind`, not on `ErrorMessage`, which is English text for humans.

```csharp
using Junevy.Communication.Core.Results;

static CommResult<int> ParsePort(string text)
{
    if (int.TryParse(text, out int port) && port is > 0 and <= 65535)
        return CommResult<int>.Success(port);

    return CommResult<int>.Fail($"Invalid port '{text}'.", CommErrorKind.InvalidRequest);
}

CommResult<int> result = ParsePort("502");
Console.WriteLine(result.IsSuccess ? $"port {result.Data}" : result.ToString());

CommResult failed = CommResult.Fail("Connection refused.", CommErrorKind.ConnectionClosed);
CommResult<string> typed = failed.As<string>();   // keeps the kind, message and exception
Console.WriteLine(typed.ErrorKind);
```

`Fail` rejects `CommErrorKind.None` with `ArgumentException`. `As<T>()` on a successful result throws `InvalidOperationException`.

`CommResult<T>.ToResult()` is the reverse for the data: it drops the data and keeps the kind, message, protocol code and exception. A successful result becomes `CommResult.Success()`.

```csharp
using Junevy.Communication.Core.Results;

CommResult<byte[]> reply = CommResult<byte[]>.Fail("No reply.", CommErrorKind.Timeout);
CommResult status = reply.ToResult();                                  // kind, message and exception kept; data dropped
Console.WriteLine(status.ErrorKind);                                   // Timeout

CommResult<byte[]> ok = CommResult<byte[]>.Success(new byte[] { 1 });
CommResult okStatus = ok.ToResult();                                   // becomes CommResult.Success()
Console.WriteLine(okStatus.IsSuccess);
```

| `CommErrorKind` | Meaning |
|---|---|
| `Unspecified` | Uncategorized failure (default for legacy paths) |
| `InvalidRequest` | Local validation failed before anything was sent |
| `ConnectionClosed` | The connection dropped, could not be opened, or a send failed |
| `Timeout` | A connect, open, handshake, send or request timeout expired |
| `ProtocolViolation` | Malformed frame, invalid length, or a correlation key that does not match |
| `RemoteError` | The peer answered with a protocol-level error code |
| `Cancelled` | The caller cancelled the operation |
| `NotConnected` | The operation was started while the channel was not connected |
| `AuthenticationFailed` | TLS certificate validation or a login step failed |
| `ResourceExhausted` | A session limit, a queue or a port was exhausted |
| `NotSupported` | The transport or protocol does not support the operation |

The values 0 to 7 match `ModbusErrorKind` numerically, so a future Modbus migration can map them directly.

## Backoff Policies

`IBackoffPolicy.GetDelay(attempt)` returns the wait in milliseconds before retry `attempt` (starting at 1), or `null` to give up.

```csharp
using Junevy.Communication.Core.Resilience;

IBackoffPolicy fixedPolicy = new FixedIntervalBackoff(interval: 500, maxAttempts: 3);
IBackoffPolicy exponential = new ExponentialBackoff(initialInterval: 1000, maxInterval: 30000);

for (int attempt = 1; attempt <= 4; attempt++)
{
    int? fixedDelay = fixedPolicy.GetDelay(attempt);   // null after 3 attempts
    int? expDelay = exponential.GetDelay(attempt);     // about 1000, 2000, 4000, 8000 ms
    Console.WriteLine($"{attempt}: fixed={fixedDelay?.ToString() ?? "give up"} exponential={expDelay}");
}
```

- `ExponentialBackoff` waits `min(maxInterval, initialInterval × multiplier^(n−1))`, with a jitter of ±20 % by default (`jitter: 0.2`). The random source is locked, so one policy instance can be shared between threads.
- `maxAttempts: 0` means unlimited for both policies.

## Timeout Scope

`TimeoutScope` links a timeout and a caller token. After it fires, `IsTimedOut` and `IsUserCancelled` tell the two causes apart, so a timeout can be reported as `Timeout` and a user cancellation as `Cancelled`.

```csharp
using Junevy.Communication.Core.Utils;

using (TimeoutScope scope = TimeoutScope.Start(timeout: 2000, userToken: CancellationToken.None))
{
    try
    {
        await Task.Delay(Timeout.Infinite, scope.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine(scope.IsTimedOut ? "timed out" : "cancelled by the caller");
    }
}
```

The optional `onAbort` callback runs at most once, when the scope is aborted by its timeout or by the caller token.

## Named Registry

`NamedRegistry<T>` maps names to instances and supports aliases. Aliases share the target instance. Removing a name removes the aliases that point to it and disposes the instance once.

```csharp
using Junevy.Communication.Core.Registry;

using var registry = new NamedRegistry<DemoConnection>();

DemoConnection bus = registry.GetOrAdd("rs485-bus", name => new DemoConnection(name));
registry.RegisterAlias("slave-1", "rs485-bus");      // the alias shares the instance

if (registry.TryGet("slave-1", out DemoConnection? found) && ReferenceEquals(found, bus))
    Console.WriteLine("slave-1 resolves to rs485-bus");

registry.TryRemove("rs485-bus");                     // removes the alias too and disposes the instance

sealed class DemoConnection : IDisposable
{
    private readonly string name;

    public DemoConnection(string name) => this.name = name;

    public void Dispose() => Console.WriteLine($"{name} disposed");
}
```

`GetOrAdd` keeps the instance that won a race and disposes the losers. `TryRemove` and `Dispose` release instances outside the registry lock.

## Hex Formatting

```csharp
using Junevy.Communication.Core.Diagnostics;

byte[] frame = { 0x01, 0x03, 0x00, 0x64, 0x00, 0x04 };
Console.WriteLine(HexFormatter.ToHex(frame));                 // 01-03-00-64-00-04
Console.WriteLine(HexFormatter.ToHex(frame, maxBytes: 4));    // long data is truncated
```

`ToHex` is intended for log messages. The channel libraries only call it when debug logging is enabled.

## Timeouts

Core has no transport timeouts. `TimeoutScope` takes its timeout as a parameter, in milliseconds.

## Platform Notes

- The public API and its behaviour are the same on `net472` and `net8.0`.
- Nullable annotations (`NotNullWhen`, `MaybeNullWhen`, `DoesNotReturn`) are available on both targets; `net472` gets them from an internal polyfill.

## More

For the complete behavioural contract of the channel family, see `Skills/using-junevy-channels/SKILL.md` in the repository.
