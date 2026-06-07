# EgressPool

EgressPool is a .NET library for sending outbound TCP, UDP, and HTTP traffic from a configured pool of source IP addresses.

Use it when an application needs its outbound connections to appear from different local addresses while keeping the calling code simple.

## What You Can Do

- Configure one or more IPv4 or IPv6 prefixes.
- Create TCP, UDP, or HTTP clients that use addresses from those prefixes.
- Reuse the same pool across many outbound requests.
- Release addresses by disposing the clients, sockets, leases, or pool you create.

## Quick Start

```csharp
using System.Net;
using Egress;

EgressPoolOptions options = new()
{
    Prefixes = [IPNetwork.Parse("127.0.0.0/8")],
};

await using EgressPool pool = await EgressPool.CreateAsync(options);
using HttpClient client = pool.CreateHttpClient();

string response = await client.GetStringAsync("http://127.0.0.1:5000/");
```

## Expected Behavior

Each outbound connection receives a source address from the configured pool. When the connection, client, or pool is disposed, the address is no longer held by that caller.

Some configurations may require operating system support or elevated permissions. If the requested behavior is not available on the current machine, pool creation or connection creation fails with an exception.

## Cleanup

Dispose the pool when the application is finished with it:

```csharp
await pool.DisposeAsync();
```

If an application exits unexpectedly, release anything left behind by a previous process:

```csharp
await EgressPool.CleanupStaleStateAsync();
```
