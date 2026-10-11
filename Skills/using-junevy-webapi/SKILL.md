---
name: using-junevy-webapi
description: Use when a .NET project references Junevy.Communication.WebApi, or needs to report data to a MES over HTTP — REST (WebApiClient) or SOAP 1.1/1.2 (SoapClient) calls, WebApiResult and ErrorKind handling, retries and idempotency keys, the business envelope ({code,msg,data}), writing interceptors (signing, token refresh, RequestReplay), subscribing to RequestSending/ResponseReceived/RequestCompleted/AvailabilityChanged, health checks and availability, log redaction, WebApiClientFactory or DI registration, or troubleshooting net472 connection limits and GBK responses
---

# Using Junevy WebApi (REST and SOAP client)

## Overview

Asynchronous REST and SOAP client for .NET (net472 + net8.0) by Junevy, version `1.0.0-preview.1`, depending on `Junevy.Communication.Core` only. It is for **actively reporting to a MES**. Core principles:

- **Result-based**: every call returns `WebApiResult<T>` (`IsSuccess`, `Data`, `StatusCode`, `RemoteCode`, `ErrorKind`, `ErrorMessage`, `Exception`). Invalid arguments, HTTP errors, timeouts and disconnects are results. Branch on `IsSuccess`, then `ErrorKind`. Never match `ErrorMessage`.
- **Exceptions only at construction**: invalid configuration, a missing credential environment variable, a `null` interceptor, or a `Serializer` given to a SOAP client throw `ArgumentException`.
- **One shared pipeline** for REST and SOAP: per-attempt timeout, retries, interceptors, events, availability, health check, disposal. The façades only build the request and interpret the response.
- **Retries are off by default** and never repeat a non-idempotent request unless told it is safe.
- **Reuse the client**: one instance = one long-lived `HttpClient`. Do not create one per call.

## When NOT to Use

- Modbus: `using-junevy-modbus`. Raw TCP/UDP/serial: `using-junevy-channels`. PLC addresses: `using-junevy-plc`.
- The MES calling the device (an HTTP **server**): not in this package yet.
- WCF proxies: keep using them; this package does not generate proxies.
- Offline caching/outbox: not implemented; `IdempotencyKey` and `AvailabilityChanged` are the hooks a future cache will use.

## REST Quick Start

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Rest;

await using var client = new WebApiClient(new WebApiClientConfig
{
    BaseUrl = "https://mes.example.com/api",
    Timeout = 5000,                                            // per attempt (ms)
    Retry = new WebApiRetryOptions { RetryCount = 2 },
    Envelope = new ResponseEnvelopeOptions { Enabled = true }, // { "code": 0, "msg": "...", "data": ... }
});

WebApiResult<ReportAck> result = await client.PostAsync<ReportAck>("reports", new { OrderNo = "工单-1" }, idempotencyKey: "report-1");
if (!result.IsSuccess)
{
    switch (result.ErrorKind)
    {
        case CommErrorKind.ConnectionClosed:
        case CommErrorKind.Timeout:
            // MES unreachable or slow: store and resend later (the idempotency key makes the resend safe).
            break;
        case CommErrorKind.RemoteError:
            // result.StatusCode and/or result.RemoteCode (business code or SOAP fault code). Not retried by the library.
            break;
    }
}

public sealed class ReportAck { public string? Id { get; set; } }
```

- Convenience methods: `GetAsync`, `PostAsync`, `PutAsync`, `DeleteAsync` (extension methods on `IWebApiClient`). Use `SendAsync(new WebApiRequest { ... })` for headers, a per-call `Timeout`, or a custom `ContentType`.
- `Path` is relative to `BaseUrl` and a leading `/` does not escape `BaseUrl`'s path. Absolute URLs, `//host/x` and a first segment containing `:` are rejected with `InvalidRequest`.
- Bodies: `byte[]`/`string` are sent as is and **need `ContentType`**; other objects use the serializer (camelCase JSON, Chinese not escaped; replace with `WebApiComponents.Serializer`).
- `TResponse = byte[]`/`string` returns the raw body (`[]`/`""` when empty, no envelope). Other types: empty 2xx body is success with `default` without an envelope; with an envelope only 204 is, other empty 2xx is `ProtocolViolation`.
- `.NET 8` has no GBK: register `CodePagesEncodingProvider.Instance` once if the MES answers `charset=gbk`.

## SOAP Quick Start

```csharp
using System.Xml.Linq;
using Junevy.Communication.WebApi.Soap;

await using var soap = new SoapClient(new SoapClientConfig { Url = "http://mes.example.com/service.asmx", Version = SoapVersion.Soap11 });

WebApiResult<GetOrderResponse> typed = await soap.InvokeAsync<GetOrder, GetOrderResponse>("urn:GetOrder", new GetOrder { OrderNo = "A-1" });
WebApiResult<XElement> raw = await soap.InvokeRawAsync("urn:GetOrder", new XElement("GetOrder", new XElement("OrderNo", "A-1")));
```

- Body types: public, parameterless constructor (`XmlSerializer`). `DefaultNamespace` applies to types without namespace attributes. `SoapCallOptions` carries `Timeout`, HTTP `Headers`, `SoapHeaders` (written into `soap:Header`) and `IdempotencyKey`.
- 1.1: `text/xml` + quoted `SOAPAction`. 1.2: `application/soap+xml; ...; action="..."`. An empty action is `""` in 1.1 and omitted in 1.2. An action containing a quote or a line break is `InvalidRequest`.
- A Fault is `RemoteError` with `RemoteCode` = fault code (whatever the HTTP status) and is **never retried**, even with an idempotency key. Responses containing a DTD are rejected (`ProtocolViolation`).

## ErrorKind Cheat Sheet

| `ErrorKind` | Meaning | Retried by the library |
|---|---|---|
| `ConnectionClosed` | DNS/refused/reset; or the client was disposed during the call | yes (if the method rule allows) |
| `Timeout` | One attempt exceeded `Timeout` (interceptors included) | yes (method rule) |
| `AuthenticationFailed` | 401 / 403 | no (use an interceptor + `RequestReplay`) |
| `ResourceExhausted` | 429 | yes (method rule; honours `Retry-After` up to 30 s) |
| `RemoteError` | other 3xx/4xx/5xx; business code not in `SuccessCodes`; SOAP Fault | only 408/500/502/503/504 **without** `RemoteCode` |
| `ProtocolViolation` | undeserializable body, body over `MaxResponseBodySize`, not a SOAP envelope, unknown charset | no |
| `InvalidRequest` | bad argument, or an interceptor changed scheme/host/port | no |
| `Unspecified` | an interceptor threw | no |
| `Cancelled` | the caller's token | no |
| `NotConnected` | called after disposal (no events are raised) | no |

## Retries and Idempotency

- `RetryCount` (default 0), `RetryDelay` (default 1000 ms, fixed; or `WebApiComponents.RetryBackoff` with any `IBackoffPolicy`).
- `GET`/`HEAD`/`OPTIONS` retry automatically. `POST`/`PUT`/`PATCH`/`DELETE` retry only with `IdempotencyKey` (sent as the `Idempotency-Key` header) or `RetryUnsafeMethods = true`. A key is a promise that the MES de-duplicates.
- A business failure (envelope code) or Fault carries a `RemoteCode` and is never retried.
- Interceptor `RequestReplay` is separate: once per call, does not use `RetryCount`, ignores the method rule.

## Hooks

Events observe; interceptors change.

| Event | Raised |
|---|---|
| `RequestSending` | before each attempt is sent (after interceptors). Not raised for attempts aborted/timed out in `OnSendingAsync` |
| `ResponseReceived` | after each sent attempt: `StatusCode` (null for network failures), `ErrorKind`, `WillRetry`, `RetryDelay`. On disposal the in-flight attempt reports `ConnectionClosed` |
| `RequestCompleted` | **exactly once per call** (also for invalid requests, `Attempts = 0`, and interceptor aborts). Handlers have finished when `SendAsync` returns |
| `AvailabilityChanged` | ordered, single consumer |

- Events run on thread-pool threads; a throwing subscriber is logged and ignored. Marshal to the UI thread yourself.
- Interceptors (`WebApiComponents.Interceptors`): `OnSendingAsync` in registration order with the config-generated auth interceptor first; `OnReceivedAsync` in reverse. Each attempt is rebuilt from the original request (no accumulation, retries are re-signed). `context.Fail(message, kind)` aborts without sending; `context.RequestReplay()` re-sends once; `context.MarkSensitiveHeader(name)` masks a header in logs/events; `context.Items` is shared by all attempts of one call.
- `RequestUri` may change in path/query only. The attempt timeout covers interceptors: honour the token. Interceptors must be thread-safe.
- Working examples (timestamp + HMAC signature; 401 → refresh token → `RequestReplay`) are in `Junevy.Communication.WebApi/README.md` and are covered by tests.

## Availability and Health Check

- `Availability`: `Unknown` → `Online` after any response except 502/503/504; `Offline` after `OfflineThreshold` (default 3) consecutive connection failures/timeouts/502-504. 4xx and business errors keep it `Online`. Cancellation and interceptor failures change nothing.
- `HealthCheckPath` (REST) / `HealthCheckUrl` (SOAP, same origin as `Url`): probing **starts at construction**, every `HealthCheckInterval` (default 30 s), through the interceptors, never retried, no request events.
- **Subscribe to `AvailabilityChanged` first, then read `Availability` once**; otherwise a change that happened during construction is missed.

## Credentials and Logging

- Credentials: environment variable names in `WebApiAuthOptions` (`Basic`/`Bearer`/`ApiKey`), read when the client is constructed (a missing/empty variable throws `ArgumentException` naming the variable only), or `WebApiAuthentication.Basic/Bearer/ApiKey` in code added to `Interceptors`. Never put secrets in configuration values.
- Logging: `Debug` per attempt; `Warning` for a final connection error/timeout/5xx. `Authorization`, `Proxy-Authorization`, interceptor-marked headers and the query parameters `access_token`, `token`, `api_key`, `apikey` print as `***` (logs and event arguments). `LogBodies` is off by default (bodies are cut at `MaxLoggedBodyLength`).

## Factory and Dependency Injection

```csharp
services.AddWebApiClients();                                   // Junevy.Communication.WebApi.DependencyInjection; idempotent
var factory = provider.GetRequiredService<WebApiClientFactory>(); // Junevy.Communication.WebApi.Factory
IWebApiClient reports = factory.GetOrAdd("reports", new WebApiClientConfig { BaseUrl = "https://mes.example.com/api" });
ISoapClient trace = factory.GetOrAdd("trace", new SoapClientConfig { Url = "http://mes.example.com/service.asmx" });
```

Same name = same instance (new config ignored). A REST request for a SOAP name (or vice versa) throws `InvalidOperationException` naming the existing type. `TryRemove` and disposing the factory dispose the client. Without a container: `WebApiClientFactoryBuilder.Create().WithLoggerFactory(f).Build()`.

## Disposal

`Dispose`/`DisposeAsync` are idempotent; they stop the health check, end in-flight calls with `ConnectionClosed`, wait up to 2 s for pending `AvailabilityChanged` events, and never dispose an injected `HttpHandler`. Later calls return `NotConnected`. Safe to call from an `AvailabilityChanged` handler.

## Common Mistakes

| Mistake | Fix |
|---|---|
| Creating a client per call | Keep one per MES endpoint (factory/DI) |
| Retrying POST by hand after `Timeout` without a key | Give the call an `IdempotencyKey` first; the request may have reached the MES |
| Expecting 200 to mean success when the MES wraps results | Enable `Envelope`; check `RemoteCode` |
| Path `"/orders"` with `BaseUrl = ".../api"` expecting the host root | It is `.../api/orders` by design |
| `Body = "text"` or `byte[]` without `ContentType` | Set `ContentType` (`InvalidRequest` otherwise) |
| Reading `Availability` before subscribing with a health check | Subscribe first, then read once |
| Doing slow work in an event handler | Handlers run inline on the call's flow (`RequestCompleted` before `SendAsync` returns); hand off to your own queue |
| Interceptor that ignores the cancellation token | The attempt timeout cannot interrupt it; pass the token to every await |
| Expecting `GBK` text on .NET 8 | Register `CodePagesEncodingProvider.Instance` at startup |
| Putting a SOAP-only client option in `WebApiComponents.Serializer` | SOAP always uses XML; setting it throws |
| net472: other code talks to the same host | The client sets `ConnectionLimit`/`ConnectionLeaseTimeout` on that host's `ServicePoint`; static `ServicePointManager` is never touched |
