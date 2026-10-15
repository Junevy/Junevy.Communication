# Junevy.Communication.OpcUa

OPC UA client for the [Junevy.Communication](https://github.com/Junevy/Junevy.Communication) communication library.

> **This version only connects to servers that offer the `None` security policy and allow anonymous logins.**
> Many real servers disable `None` by default; connecting to those returns `NotSupported`.
> Secure channels (Sign / SignAndEncrypt), client certificates and username logins are not implemented yet.

- **Client only.** OPC UA servers are not part of this package.
- **.NET 8 only.** Unlike the other packages of this repository, this one does not target .NET Framework 4.7.2, because the OPC UA 2.0 line no longer does.

The OPC UA protocol itself — the secure channel, sessions, the binary encoding, browsing and subscriptions — is handled by the OPC Foundation's .NET Standard Stack ([MIT](https://github.com/OPCFoundation/UA-.NETStandard)). This package is a thin adapter around it: it exposes its own types, keeps the SDK out of the public API and turns every SDK failure into a `CommResult`.

## Dependency footprint

`OPCFoundation.NetStandard.Opc.Ua.Client` 2.0.1 pulls `Microsoft.Extensions.*` **10.0.12 or newer** (logging, options, dependency injection, Http, telemetry) into your application, plus `System.Text.Json` 10.0.12, `System.IO.Pipelines` 10.0.12, `System.Threading.RateLimiting` 10.0.12, `Polly` 8.4.2 and `BitFaster.Caching` 2.6.1. This is a consequence of the SDK, not of this package. If your application pins an older `Microsoft.Extensions.*`, raise it to the SDK's version; otherwise NuGet reports `NU1605` (package downgrade).

## Getting started

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa;

OpcUaClient client = OpcUaClient.Create(new OpcUaClientOptions
{
    EndpointUrl = "opc.tcp://127.0.0.1:4840/",
    ApplicationName = "line1-plc",
});

CommResult connected = await client.ConnectAsync();
if (!connected.IsSuccess)
{
    Console.WriteLine(connected);
    return;
}

CommResult<IReadOnlyList<OpcUaValue>> values = await client.ReadAsync(new[] { "ns=2;s=Line1.Speed" });
CommResult written = await client.WriteAsync("ns=2;s=Line1.Speed", 1234.5);
CommResult<IReadOnlyList<OpcUaReference>> children = await client.BrowseAsync("i=85");
```

`Junevy.Communication.OpcUa/README.md` in the repository carries the full reference: NodeId syntax, the value type table, batch semantics, subscriptions, reconnect, options, the error table and dependency injection.