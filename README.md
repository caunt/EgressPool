# EgressPool

Small .NET 10 library for distributing outbound TCP, UDP, and HTTP connections across configured IPv4 or IPv6 source prefixes.

## Projects

- `src/EgressPool` - core library.
- `src/EgressPool.DependencyInjection` - `IServiceCollection` and `IHttpClientFactory` integration.
- `samples/EgressPool.Sample` - minimal loopback HTTP sample.
- `tests/EgressPool.Tests` - unit and behavioral tests.

## Features

- TCP, UDP, and HTTP egress address selection.
- HTTP integration through `SocketsHttpHandler.ConnectCallback`.
- Address modes: `NonLocalBind`, `AssignOnDemand`, and `PreAssignedOnly`.
- Interface selection: explicit, default route, per-destination route, or custom callback.
- Cleanup tracking for addresses and routes created by the library.

## Quick Start

```csharp
using System.Net;
using Egress;

EgressPoolOptions options = new()
{
    Prefixes = [IPNetwork.Parse("127.0.0.0/8")],
    AddressMode = EgressAddressMode.NonLocalBind,
    InterfaceSelectionMode = EgressInterfaceSelectionMode.Explicit,
    InterfaceName = "lo",
    ManageLocalRoutes = false,
};

await using EgressPool pool = await EgressPool.CreateAsync(options);
using HttpClient client = pool.CreateHttpClient();

string response = await client.GetStringAsync("http://127.0.0.1:5000/");
```

## Commands

```bash
dotnet build EgressPool.slnx
dotnet test EgressPool.slnx
dotnet run --project samples/EgressPool.Sample/EgressPool.Sample.csproj
```

Coverage:

```bash
dotnet test EgressPool.slnx --collect:"XPlat Code Coverage"
```

## Platform Notes

`NonLocalBind` fast mode is Linux-specific. `AssignOnDemand` modifies OS network configuration and may require elevated privileges. Tests avoid privileged network changes and use loopback plus fake platform behavior.

## Cleanup

The library removes owned addresses and routes when leases, sockets, handlers, clients, or pools are disposed. Stale owned state can be recovered with:

```csharp
await EgressPool.CleanupStaleStateAsync();
```
