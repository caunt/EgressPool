# EgressPool

EgressPool is a .NET library for sending outbound TCP, UDP, and HTTP traffic from a configured pool of source IP addresses.

Use it when an application needs its outbound connections to appear from different local addresses while keeping the calling code simple.

## What You Can Do

- Configure one or more IPv4 or IPv6 prefixes.
- Automatically detect prefixes allocated on local interfaces.
- Create TCP, UDP, or HTTP clients that use addresses from those prefixes.
- Select prefixes by destination scope for TCP, HTTP, and destination-aware UDP clients.
- Reuse the same pool across many outbound requests.
- Release addresses by disposing the clients, sockets, leases, or pool you create.

## Quick Start

```csharp
using Egress;

await using EgressPool pool = await EgressPool.CreateAsync();
using HttpClient client = pool.CreateHttpClient();

string response = await client.GetStringAsync("http://127.0.0.1:5000/");
```

## Expected Behavior

Each outbound connection receives a source address from the configured pool. When the connection, client, or pool is disposed, the address is no longer held by that caller.

Some configurations may require operating system support or elevated permissions. If the requested behavior is not available on the current machine, pool creation or connection creation fails with an exception.
