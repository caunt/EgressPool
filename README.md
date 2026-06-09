# EgressPool

A .NET library for sending outbound TCP, UDP, and HTTP traffic from a pool of source IP addresses. Outbound connections appear from different local addresses while the calling code stays simple.

## Features

- Configure IPv4 or IPv6 prefixes, or auto-detect them from local interfaces.
- Create TCP, UDP, or HTTP clients bound to addresses from those prefixes.
- Select prefixes by destination scope.
- Release addresses by disposing clients, sockets, leases, or the pool itself.

## Quick Start

```csharp
using Egress;

await using EgressPool pool = await EgressPool.CreateAsync();
using HttpClient client = pool.CreateHttpClient();

string response = await client.GetStringAsync("http://127.0.0.1:5000/");
```

## Dependency Injection

Install `EgressPool.DependencyInjection` and register with your service collection:

```csharp
builder.Services.AddEgressPool();
builder.Services.AddHttpClient("egress").UseEgressPool();
```

## Running in Containers

Containers need host networking, root access, and the `NET_ADMIN` capability so the pool can bind to and configure host interfaces.

```bash
docker run --network host --user root --cap-add NET_ADMIN your-image
```

Docker Compose equivalent:

```yaml
services:
  app:
    image: your-image
    network_mode: host
    user: root
    cap_add:
      - NET_ADMIN
```

## Expected Behavior

Each outbound connection uses a source address from the pool. Disposing the connection, client, or pool releases the address.

Some configurations require elevated permissions. If unavailable, pool or connection creation throws an exception.
