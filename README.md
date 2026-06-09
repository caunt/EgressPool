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

## Dependency Injection

Install the `EgressPool.DependencyInjection` package and register the pool with your service collection:

```csharp
builder.Services.AddEgressPool();
builder.Services.AddHttpClient("egress").UseEgressPool();
```

## Running in Containers

When running inside a container, the following requirements must be met:

- **Host network mode** – the container must use the host network stack (`--network host` in Docker) so that the pool can see and bind to the host's network interfaces.
- **Root user** – the process must run as root (`--user root` or `USER root` in the Dockerfile).
- **NET_ADMIN capability** – the container must be granted the `NET_ADMIN` Linux capability (`--cap-add NET_ADMIN` in Docker) so that the pool can configure source addresses on the interfaces.

Example `docker run` command:

```bash
docker run --network host --user root --cap-add NET_ADMIN your-image
```

Or in a `docker-compose.yml`:

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

Each outbound connection receives a source address from the configured pool. When the connection, client, or pool is disposed, the address is no longer held by that caller.

Some configurations may require operating system support or elevated permissions. If the requested behavior is not available on the current machine, pool creation or connection creation fails with an exception.
