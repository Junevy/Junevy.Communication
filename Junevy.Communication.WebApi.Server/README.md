# Junevy.Communication.WebApi.Server

An HTTP server for MES on an embedded Kestrel. The host registers routes and handlers; the MES calls them with HTTP and JSON.

**Supported:** .NET 8 only. REST and JSON only; there is no SOAP server.

## Requirements

- The target machine needs the **ASP.NET Core 8 runtime**. For a framework-dependent deployment install "ASP.NET Core Runtime 8". The .NET Desktop Runtime alone is not enough. A self-contained publish includes the runtime.
- The library does not use `netsh`, URL ACLs or certificate bindings. Open the port in the firewall.

## Quick start

Register every route before `StartAsync`. Each start builds a new Kestrel application, so a stopped server can start again.

```csharp // sample: order-model
public sealed class Order
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
```

```csharp // sample: quick-start
public static class OrderService
{
    public static async Task<WebApiServer> StartAsync(string url)
    {
        var server = new WebApiServer("mes", new WebApiServerConfig { Urls = new[] { url } });
        server.MapGet<Order>("/orders/{id:int}", (context, _) =>
            Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = int.Parse(context.RouteValues["id"]) })));
        server.MapPost<Order, Order>("/orders", (order, _, _) =>
            Task.FromResult(WebApiServerResponse<Order>.Ok(order, 201)));

        CommResult started = await server.StartAsync();
        if (!started.IsSuccess)
            throw new InvalidOperationException(started.ErrorMessage);

        return server;
    }
}
```

`StartAsync` returns a `CommResult` and does not throw for an address in use, missing routes or a bad certificate. Check `IsSuccess` and `ErrorKind`. Use `http://*:8080` to listen on all interfaces, or `http://127.0.0.1:0` to let the system pick a free port; after the start, `Urls` holds the bound addresses.

## Routes

Templates are ASP.NET Core route templates: `/orders/{id:int}` and the other constraints work as usual, and a literal segment wins over a parameter. A request that matches no route gets 404. A request whose path matches a route but whose method does not gets 405 with an `Allow` header. Both use the library's response format.

The methods are `MapGet`, `MapDelete` (no body), and `MapPost`, `MapPut` (JSON body). The same method and template cannot be registered twice.

## Responses

Without the envelope, a success writes the data itself (200). Null data gives 204 with no body. A business failure is an RFC 9457 Problem Details document with the `application/problem+json` content type; the business code is in the `code` member, and the status is 400 unless the handler says otherwise.

With the envelope enabled, every body is `{ code, msg, data }` at the configured paths. The sample below gives:

- success: `{ "result": { "code": "0", "msg": "", "data": { "id": 1, "name": "a" } } }` with status 201;
- business failure: `{ "result": { "code": "E100", "msg": "the order id must be positive" } }` with status `BusinessFailureStatusCode` (200 by default).

A handler can set an explicit status code; it always wins.

```csharp // sample: envelope
public static class EnvelopeService
{
    public static WebApiServer Create(string url)
    {
        var config = new WebApiServerConfig
        {
            Urls = new[] { url },
            Envelope = new ResponseEnvelopeOptions
            {
                Enabled = true,
                CodePath = "result.code",
                MessagePath = "result.msg",
                DataPath = "result.data",
                SuccessCodes = new[] { "0" },
            },
        };
        var server = new WebApiServer("mes", config);
        server.MapPost<Order, Order>("/orders", (order, _, _) => Task.FromResult(order.Id > 0
            ? WebApiServerResponse<Order>.Ok(order, 201)
            : WebApiServerResponse<Order>.Fail("E100", "the order id must be positive")));
        return server;
    }
}
```

## Request bodies

For POST and PUT the body is read as follows. An empty body gives 400. A body whose `Content-Type` is not JSON (`application/json`, or `*/*+json`) gives 415. A `byte[]` parameter receives the raw bytes, and a `string` parameter receives the text decoded with the `charset` parameter (UTF-8 by default; charsets the runtime does not provide give 415). Any other type is deserialized by the serializer; a body it cannot deserialize gives 400 with no details in the response. A body larger than `MaxRequestBodySize` (1 MiB by default) gives 413.

## Concurrency and timeouts

`MaxConcurrentRequests` (16 by default) limits the requests in progress. A request beyond the limit gets 503 with `Retry-After: 1` at once; nothing is queued.

`RequestTimeout` (30 seconds by default) covers reading the body, the interceptors and the handler. Reading the body past the timeout gives 408; anything later gives 504. A handler should honour its `CancellationToken`. A handler that ignores it keeps running after the 504 was sent, its result is discarded, and a warning is logged. An exception in the handler gives 500 with a generic message; the exception goes to the log only.

## Authentication

`Auth` (no authentication by default) accepts a scheme and the **names** of environment variables that hold the secrets:

- `Basic`: `UserNameVariable` and `PasswordVariable`;
- `Bearer`: `TokenVariable`;
- `ApiKey`: `ApiKeyHeader` (default `X-API-Key`) and `ApiKeyVariable`.

The secrets are read when the server is created, and a missing variable is reported by name only. Comparisons are constant-time. A failure gives 401; Basic and Bearer add a `WWW-Authenticate` challenge, and the response never repeats the expected value.

A custom `IWebApiRequestAuthenticator` in `WebApiServerComponents.Authenticator` replaces the configured scheme. If the server listens on all interfaces without any authentication, `StartAsync` logs a warning.

## Interceptors and events

An interceptor derives from `WebApiServerInterceptor` and overrides one or both stages. The request stage runs in registration order and stops at the first `Reject`, so the handler does not run. The response stage runs in reverse order, only for the interceptors whose request stage completed, and can add headers.

```csharp // sample: interceptor
public sealed class RequireDeviceKey : WebApiServerInterceptor
{
    public override Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.Headers.ContainsKey("X-Device-Key"))
            context.Reject(403, "E403", "the device key is missing");
        return Task.CompletedTask;
    }
}
```

`RequestReceived` is raised before routing. `RequestCompleted` is raised once for every request, including rejected, timed-out, disconnected and replayed ones. It carries the `Outcome`, the status code, the route template and the elapsed time. A subscriber that throws is logged, and the other subscribers and the response are unaffected.

## Idempotent replay (deduplication)

The MES may repeat a POST after a timeout, and a repeated command must not run twice. Enable deduplication and have the MES send the same `Idempotency-Key` header with the retry. The key, method and path together identify a request, and the body is compared by hash.

```csharp // sample: deduplication
public static class DeduplicatedService
{
    public static WebApiServer Create(string url)
    {
        var config = new WebApiServerConfig
        {
            Urls = new[] { url },
            Deduplication = new WebApiServerDeduplicationOptions { Enabled = true },
        };
        var server = new WebApiServer("mes", config);
        server.MapPost<Order, Order>("/orders", (order, _, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(order, 201)));
        return server;
    }
}
```

Within the window (10 minutes by default) a completed request is answered again with its first status, content type and body, plus `Idempotent-Replayed: true`, and the handler does not run. The same key while the first request is still running gets 409. The same key with a different body gets 422. Only the handler's own results are kept: framework rejections, body errors and exceptions are not, so a retry runs the handler again. When the cache is full, the oldest completed entry is evicted; if every entry is still running, the new request gets 503.

## Lifecycle

States are `Stopped`, `Starting`, `Running` and `Stopping`. `StateChanged` reports every change in order. `StopAsync` stops accepting requests, waits up to `StopTimeout` (5 seconds by default) for the requests in progress, then aborts the rest. It can be called again. `Dispose` and `DisposeAsync` stop the server and release it; afterwards `StartAsync` and the `Map` methods throw `ObjectDisposedException`, and `StopAsync` does nothing.

`StartAsync` returns `InvalidRequest` for a second start, no routes, or a route template that cannot be mapped; `ResourceExhausted` for an address already in use; `AuthenticationFailed` for an address the system reserves or refuses to bind; and `InvalidRequest` for a certificate that cannot be loaded. A failed start leaves nothing running.

## Status codes

| Situation | HTTP status | Outcome |
|---|---|---|
| The handler succeeds | 200 (204 without data and without the envelope; an explicit status wins) | `Succeeded` |
| The handler returns a business failure | the envelope's `BusinessFailureStatusCode` (200), otherwise 400; an explicit status wins | `BusinessFailure` |
| Empty body, or a body that cannot be deserialized | 400 | `Rejected` |
| A body with a non-JSON `Content-Type` | 415 | `Rejected` |
| Authentication fails | 401, with `WWW-Authenticate` for Basic and Bearer | `Rejected` |
| No route matches the path | 404 | `Rejected` |
| The path matches, the method does not | 405, with `Allow` | `Rejected` |
| The body is not read in time | 408 | `Rejected` |
| The same key is still running | 409 | `Rejected` |
| The body is larger than `MaxRequestBodySize` | 413 | `Rejected` |
| The same key with a different body | 422 | `Rejected` |
| An interceptor rejects | the interceptor's status | `Rejected` |
| The handler or an interceptor throws | 500, with a generic message | `HandlerFailed` |
| Too many concurrent requests, or a full dedup cache | 503, with `Retry-After: 1` | `Rejected` |
| The handler does not finish in time | 504 | `TimedOut` |
| A deduplicated replay | the first response, with `Idempotent-Replayed: true` | `Replayed` |
| The client disconnected before the response | nothing is written | `ClientDisconnected` |

## HTTPS

Set `Https.CertificatePath` to a `.pfx` file and name the variable that holds its password in `Https.CertificatePasswordVariable`, or pass an `X509Certificate2` in `WebApiServerComponents.Certificate` (the server does not dispose it). The certificate is loaded at start; a failure is reported as `InvalidRequest`.

## Factory and dependency injection

`WebApiServerFactory` creates servers by name, like the client factory. `AddWebApiServers()` registers the factory as a singleton and can be called together with `AddWebApiClients()`. The container's `ILoggerFactory` is used for the servers' logs.

## Deployment

Open the port in the firewall for the MES. If the host is already an ASP.NET Core application, use Minimal API directly instead of this package.

## Not verified

This package has not been tested against a real MES. Streaming bodies, multipart uploads, CORS, OpenAPI generation and HTTP/2 configuration are not part of this version.
