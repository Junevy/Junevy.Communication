# Junevy.Communication.WebApi

An asynchronous REST and SOAP client for **actively reporting production data to a MES** (and any other HTTP service on the shop floor). It adds what a bare `HttpClient` does not give you: per-attempt timeouts, safe retries (idempotency-aware), availability tracking with a health check, request/response hooks (events to observe, interceptors to change), log redaction, and a result type that never throws at run time.

> Preview (`1.0.0-preview.1`): the public API may change before the P3 milestone is released. The HTTP **server** side (the MES calling the device) is not part of this package yet.

## Target Frameworks

- .NET Framework 4.7.2 (`net472`)
- .NET 8 (`net8.0`)

## Installation

```text
dotnet add package Junevy.Communication.WebApi --prerelease
```

The package depends on `Junevy.Communication.Core` only (plus `System.Text.Json` on net472).

## Quick start: REST

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Rest;

await using var client = new WebApiClient(new WebApiClientConfig
{
    BaseUrl = "https://mes.example.com/api",
    Timeout = 5000,                                            // per attempt, in milliseconds
    Retry = new WebApiRetryOptions { RetryCount = 2 },
    Envelope = new ResponseEnvelopeOptions { Enabled = true }, // the MES answers { "code": 0, "msg": "...", "data": ... }
});

// The idempotency key makes the POST safe to retry (see "Retries and idempotency").
WebApiResult<ReportAck> result = await client.PostAsync<ReportAck>(
    "reports", new { OrderNo = "工单-1", Quantity = 12 }, idempotencyKey: "report-1");

if (result.IsSuccess)
    Console.WriteLine(result.Data!.Id);
else
    Console.WriteLine($"{result.ErrorKind}: {result.ErrorMessage} (HTTP {result.StatusCode}, business code {result.RemoteCode})");

public sealed class ReportAck
{
    public string? Id { get; set; }
}
```

Reuse the client: one instance owns one long-lived `HttpClient`. Creating a client per call defeats connection reuse.

## Quick start: SOAP

```csharp
using System.Xml.Linq;
using Junevy.Communication.WebApi.Soap;

await using var soap = new SoapClient(new SoapClientConfig
{
    Url = "http://mes.example.com/service.asmx",
    Version = SoapVersion.Soap11,
    DefaultNamespace = "http://mes.example.com/ns",   // for body types without namespace attributes
});

// Typed: the body is serialized with XmlSerializer, the first child of the response Body is deserialized.
WebApiResult<GetOrderResponse> typed = await soap.InvokeAsync<GetOrder, GetOrderResponse>("urn:GetOrder", new GetOrder { OrderNo = "A-1" });

// Raw XML: no modelling needed.
WebApiResult<XElement> raw = await soap.InvokeRawAsync("urn:GetOrder", new XElement("GetOrder", new XElement("OrderNo", "A-1")));
```

A SOAP Fault always becomes `RemoteError` with `RemoteCode` = the fault code, whatever the HTTP status, and is never retried. Body types must be public with a public parameterless constructor (an `XmlSerializer` requirement).

## Results and error kinds

Every call returns a `WebApiResult<T>`; invalid arguments, HTTP errors, timeouts and disconnects are results, not exceptions. Branch on `IsSuccess`, then on `ErrorKind`; never match `ErrorMessage` text. Exceptions are thrown only at construction (invalid configuration).

| Situation | `ErrorKind` | `StatusCode` / `RemoteCode` |
|---|---|---|
| DNS failure, connection refused or reset | `ConnectionClosed` | none |
| Attempt timed out | `Timeout` | none |
| 401 / 403 | `AuthenticationFailed` | status code |
| 429 | `ResourceExhausted` | 429 |
| Other 3xx / 4xx / 5xx (redirects are not followed) | `RemoteError` | status code |
| SOAP Fault; envelope business code not in `SuccessCodes` | `RemoteError` | status code; fault code / business code |
| Response cannot be deserialized; body over `MaxResponseBodySize`; not a SOAP envelope | `ProtocolViolation` | status code |
| Invalid request (absolute path, missing `ContentType`, `Timeout <= 0`, interceptor changed the host) | `InvalidRequest` | none |
| An interceptor threw | `Unspecified` | none |
| The caller's token was cancelled | `Cancelled` | none |
| The client was disposed (before the call) | `NotConnected` | none |
| The client was disposed while the call was in flight | `ConnectionClosed` | none |

## Paths, queries and bodies (REST)

- `Path` is **relative to `BaseUrl`**, and a leading `/` does not reset to the host root: with `BaseUrl = "http://host/api"`, both `orders` and `/orders` call `http://host/api/orders`. `BaseUrl` may or may not end with `/`.
- An absolute reference is rejected with `InvalidRequest` so that credentials cannot be sent to another host: `http://other/x`, `//other/x`, `\\other\x`, and any path whose **first segment contains a colon** (`orders:1`, which is indistinguishable from a URL scheme; write `./orders:1`).
- `Query` names and values are escaped with `Uri.EscapeDataString`; entries with a `null` value are not sent.
- Body: a `byte[]` or `string` is sent as is and **requires `ContentType`** (a string is encoded with the charset of the content type, UTF-8 by default). Any other object goes through the serializer (default: `System.Text.Json`, camelCase, case-insensitive reading, **Chinese text is not escaped**). Replace the serializer with `WebApiComponents.Serializer`.
- Headers are merged as: `DefaultHeaders` → `WebApiRequest.Headers` (same name wins) → the idempotency header.

### Response data

| `TResponse` | What you get |
|---|---|
| `byte[]` | the raw body (an empty array when the body is empty), no envelope handling |
| `string` | the raw body decoded with the response charset (UTF-8 if none; `""` when empty), no envelope handling |
| anything else | deserialized by the serializer; with the envelope enabled, the `data` field |

Empty bodies: with `byte[]` / `string` they are `[]` / `""`. For other types, without an envelope an empty 2xx body is success with `Data = default`; with the envelope enabled only **204** is success, any other empty 2xx is a `ProtocolViolation` (the business outcome cannot be determined).

Business envelope (`ResponseEnvelopeOptions`): many MES systems answer HTTP 200 whatever happens and put the outcome in the body. With `Enabled = true`, `CodePath` / `MessagePath` / `DataPath` (dotted paths such as `result.code`, matched case-sensitively) locate the fields with `JsonDocument` (independent of the serializer); a code outside `SuccessCodes` is `RemoteError` with `RemoteCode` = the code. Codes compare by their JSON text, so `0` and `"0"` are the same code. A business failure is **never retried**: the request reached the business layer.

> GBK and other code pages: .NET 8 does not ship them. If a MES answers `charset=gbk`, register `CodePagesEncodingProvider.Instance` once at startup (`Encoding.RegisterProvider`); the library never changes global settings. An unknown charset is a `ProtocolViolation`, not a silent fall back to UTF-8.

## Hooks: events observe, interceptors change

Use **events** to log, count or display; use **interceptors** to change a request (sign it, swap a token, add a header) or to decide something about a response. Events cannot await and cannot modify; interceptors run before every attempt, so a retry is signed again.

### Event sequence

```text
call starts
 └─ attempt n
     ├─ interceptors OnSendingAsync        (registration order; the config-generated auth interceptor first)
     ├─ RequestSending                     (not raised if an interceptor failed, threw, changed the host, or timed out here)
     ├─ HTTP exchange
     ├─ interceptors OnReceivedAsync       (reverse order; not called when there was no HTTP response)
     └─ ResponseReceived                   (WillRetry / RetryDelay already decided; not raised for attempts that never sent)
        … next attempt after the retry delay, or the interceptor-requested replay …
 └─ RequestCompleted                        (exactly once per call)
```

- `RequestCompleted` is raised **exactly once per call**, including calls rejected before any attempt (`Attempts = 0`) and calls ended by an interceptor. After the client is disposed, calls return `NotConnected` and raise nothing.
- An attempt that is aborted by an interceptor (`Fail`, an exception, a changed host) or that times out **inside `OnSendingAsync`** raises neither `RequestSending` nor `ResponseReceived`, and does not count for availability.
- When disposal ends an in-flight attempt, that attempt's `ResponseReceived.ErrorKind`, the call result and `RequestCompleted` are all `ConnectionClosed`.
- Request events run synchronously on the call's own thread-pool flow, in order, and `RequestCompleted` handlers have finished before `SendAsync` returns. A subscriber that throws is logged and does not affect the call or other subscribers. Marshal to the UI thread yourself.

### Interceptors

Implement `IWebApiInterceptor` (or derive `WebApiInterceptor` and override one method). Add them to `WebApiComponents.Interceptors`. Contexts only **record intent**: `Fail(message, kind)` ends the call without sending (and without retry), `RequestReplay()` asks for one immediate re-send, `MarkSensitiveHeader(name)` masks a header you wrote in logs and events.

- Order: `OnSendingAsync` runs in registration order with the interceptor generated from `WebApiAuthOptions` first; `OnReceivedAsync` runs in **reverse** order (like `DelegatingHandler`).
- Each attempt starts from the original request: your changes from the previous attempt do not accumulate (nothing is signed twice). `Items` is shared by all interceptors and all attempts of one call.
- The attempt timeout covers the interceptors, the exchange and reading the body: honour the `CancellationToken` you are given.
- You may change `RequestUri` (path and query only; scheme, host and port must stay, otherwise the call fails with `InvalidRequest`), `Headers`, `Body` and `ContentType`. In `OnReceivedAsync` you may replace `Body` (for example to decrypt it).
- A call replays at most once; a replay does not count against `RetryCount` and ignores the method rule (the interceptor is responsible). `Fail` wins over `RequestReplay`.
- Interceptors are shared by concurrent calls: keep them thread-safe.

**Example 1: timestamp and signature** (runs again on every attempt, so a retry carries a fresh timestamp and signature):

```csharp
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.WebApi;

/// <summary>Adds a timestamp and an HMAC signature before every attempt. It runs again for every attempt (retries included), so both are always fresh.</summary>
public sealed class SigningInterceptor : WebApiInterceptor
{
    private readonly byte[] secret;
    private readonly Func<DateTimeOffset> clock;

    public SigningInterceptor(string secret, Func<DateTimeOffset>? clock = null)
    {
        this.secret = Encoding.UTF8.GetBytes(secret);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public override Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
    {
        string timestamp = clock().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var sha256 = SHA256.Create();
        string bodyHash = Convert.ToBase64String(sha256.ComputeHash(context.Body));
        string payload = $"{context.Method}\n{context.RequestUri.PathAndQuery}\n{timestamp}\n{bodyHash}";

        using var hmac = new HMACSHA256(secret);
        string signature = BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", string.Empty).ToLowerInvariant();

        context.Headers["X-Timestamp"] = timestamp;
        context.Headers["X-Signature"] = signature;
        context.MarkSensitiveHeader("X-Signature"); // logs and event arguments show *** instead of the value
        return Task.CompletedTask;
    }
}
```

**Example 2: 401, refresh the token, replay once**:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Junevy.Communication.WebApi;

/// <summary>Sends the current token with every attempt; on a 401 it refreshes the token and asks for one replay.</summary>
public sealed class TokenRefreshInterceptor : WebApiInterceptor
{
    private const string RefreshedKey = "token-refreshed";

    private readonly Func<CancellationToken, Task<string>> refreshToken;
    private string token;

    public TokenRefreshInterceptor(string initialToken, Func<CancellationToken, Task<string>> refreshToken)
    {
        token = initialToken;
        this.refreshToken = refreshToken;
    }

    public override Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
    {
        context.Headers["Authorization"] = "Bearer " + Volatile.Read(ref token);
        context.MarkSensitiveHeader("Authorization");
        return Task.CompletedTask;
    }

    public override async Task OnReceivedAsync(WebApiReceivedContext context, CancellationToken cancellationToken)
    {
        // Items is shared by all attempts of one call, so a call refreshes the token at most once.
        if (context.StatusCode != 401 || context.Items.ContainsKey(RefreshedKey))
            return;

        Volatile.Write(ref token, await refreshToken(cancellationToken).ConfigureAwait(false));
        context.Items[RefreshedKey] = true;
        context.RequestReplay(); // send again right away; OnSendingAsync runs again and picks up the new token
    }
}
```

Register both:

```csharp
var components = new WebApiComponents();
components.Interceptors.Add(new SigningInterceptor(Environment.GetEnvironmentVariable("MES_SIGN_SECRET")!));
components.Interceptors.Add(new TokenRefreshInterceptor(initialToken, RefreshTokenAsync));
var client = new WebApiClient(config, logger, components);
```

Both examples are compiled and run by `ReadmeSampleTests` in the test project, which also checks that this README contains them verbatim.

## Retries and idempotency

Retries are **off** by default (`Retry.RetryCount = 0`). Reporting is mostly `POST`, and a repeated submission can double-count a pass or a quantity, so the rules are deliberately more conservative than RFC 9110.

| Question | Rule |
|---|---|
| Which results can be retried? | A network failure (`ConnectionClosed`), an attempt timeout, or HTTP 408 / 429 / 500 / 502 / 503 / 504 |
| Never retried | Any result with a `RemoteCode` (business failure, SOAP Fault), 4xx other than 408 / 429, oversize responses, `InvalidRequest`, `Unspecified`, `Cancelled` |
| Which methods? | `GET`, `HEAD`, `OPTIONS` automatically. `POST`, `PUT`, `PATCH`, `DELETE` only when the call has an **`IdempotencyKey`**, or `Retry.RetryUnsafeMethods = true` |
| The idempotency key | Sent in the `Idempotency-Key` header (`IdempotencyHeaderName`, empty = not sent); the MES must use it to de-duplicate |
| Delay | `Retry.RetryDelay` (fixed, default 1000 ms), or `WebApiComponents.RetryBackoff` (any `IBackoffPolicy`, for example `ExponentialBackoff`) |
| `Retry-After` | On 429 and 503, seconds or an HTTP date raise the delay (never lower it); more than 30 s means no retry |
| Replay | An interceptor's `RequestReplay` is separate: once per call, not counted, no method rule |
| Cancelling | A cancelled token during the delay ends the call with `Cancelled`; disposing ends it with `ConnectionClosed` |

## Availability and health checks

`IWebApiEndpoint.Availability` is `Unknown`, `Online` or `Offline`. Any response other than 502 / 503 / 504 marks the endpoint `Online` and resets the counter; connection failures, attempt timeouts and 502 / 503 / 504 count as failures, and `OfflineThreshold` (default 3) consecutive failures mark it `Offline`. Cancellation, invalid requests and interceptor failures change nothing. 4xx and business errors prove the MES is reachable.

`AvailabilityChanged` is raised in order by a single consumer (a slow subscriber never slows requests). It is the trigger for a future offline cache to resend what was stored.

**Subscribe, then read `Availability` once.** A client with `HealthCheckPath` (REST) or `HealthCheckUrl` (SOAP; must have the same scheme, host and port as `Url`) **starts probing when it is constructed**, so the first change can happen before you had a chance to subscribe. Subscribe first, then read `Availability` for the current state; later changes arrive as events.

The probe is a `GET` every `HealthCheckInterval` (default 30 s) that goes through the interceptor chain and the timeout, is never retried, raises no request events and only feeds availability. Any HTTP answer counts as reachable (502 / 503 / 504 as above).

## Logging and redaction

Pass an `ILogger<WebApiClient>` / `ILogger<SoapClient>` (or a logger factory to the client factory). Each attempt logs method, URI, status and elapsed time at `Debug`; a call that finally fails with a connection error, a timeout or a 5xx logs a `Warning`; other failures log at `Debug`.

- Header values listed in `Logging.RedactedHeaders` (default `Authorization`, `Proxy-Authorization`), headers your interceptors marked sensitive, and query parameter values listed in `Logging.RedactedQueryParameters` (default `access_token`, `token`, `api_key`, `apikey`) are shown as `***` in logs **and** in event arguments. The request that is actually sent is not changed.
- Bodies are logged only with `Logging.LogBodies = true`, cut at `MaxLoggedBodyLength` characters with the original length noted. Bodies can contain business data or credentials; leave this off in production.

## Credentials

Credentials never live in configuration objects. Either name **environment variables** in `WebApiAuthOptions` (read once when the client is constructed; a missing or empty variable throws `ArgumentException` naming the variable, never its value):

```csharp
Auth = new WebApiAuthOptions { Scheme = WebApiAuthScheme.Bearer, TokenVariable = "MES_TOKEN" }
```

or inject them in code with the factories in `Junevy.Communication.WebApi.Auth.WebApiAuthentication` (`Basic`, `Bearer` with a fixed token or a provider called before every attempt, `ApiKey`) and add the result to `WebApiComponents.Interceptors`. Logging in to obtain a token, signing and other schemes are interceptors (see the examples).

## HttpClient behaviour and platform notes

- One long-lived `HttpClient` per client; connections are recycled every 5 minutes so DNS changes are picked up. Redirects are **not** followed (a redirect would turn a `POST` into a `GET` and drop the body); gzip and deflate responses are decompressed.
- `MaxConnectionsPerServer` (default 10) limits concurrent connections to the host.
- `WebApiComponents.HttpHandler` injects your own handler (proxy, client certificate, tests). The client **does not dispose** an injected handler.
- **net472:** the .NET Framework allows only 2 connections per host by default and keeps connection settings on a `ServicePoint` shared by everything in the process that talks to the same host. The client configures only the `ServicePoint` of its own endpoint (`ConnectionLimit`, `ConnectionLeaseTimeout`); it never touches the static `ServicePointManager` settings. Other code in the same process that talks to the same host sees those two settings. `Expect: 100-continue` is turned off on every request.
- Every response body is read completely and limited to `MaxResponseBodySize` (default 4 MiB): the `Content-Length` is checked first, then the bytes are counted while reading.

## Factory and dependency injection

```csharp
using Junevy.Communication.WebApi.DependencyInjection;
using Junevy.Communication.WebApi.Factory;

services.AddWebApiClients();                      // one WebApiClientFactory singleton; idempotent

WebApiClientFactory factory = provider.GetRequiredService<WebApiClientFactory>();
IWebApiClient reports = factory.GetOrAdd("reports", new WebApiClientConfig { BaseUrl = "https://mes.example.com/api" });
ISoapClient trace = factory.GetOrAdd("trace", new SoapClientConfig { Url = "http://mes.example.com/service.asmx" });
```

`GetOrAdd` returns the same instance for the same name (the new configuration is ignored); asking for a REST client under a name that holds a SOAP client (or the reverse) throws `InvalidOperationException` naming the existing type. Removing a client (`TryRemove`) or disposing the factory disposes it. Without a container use `WebApiClientFactoryBuilder.Create().WithLoggerFactory(...).Build()`.

## Disposal

`Dispose` / `DisposeAsync` are idempotent: they stop the health check, end in-flight calls with `ConnectionClosed`, wait up to 2 seconds for pending `AvailabilityChanged` events to be delivered (the rest are dropped with a warning), and release the client's own `HttpClient`. Calls after disposal return `NotConnected` and raise no events. Calling `Dispose` from inside an `AvailabilityChanged` handler is safe.
