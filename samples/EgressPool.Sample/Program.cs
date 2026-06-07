using System.Net;
using Egress;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();

WebApplication app = builder.Build();
app.MapGet("/", (HttpContext context) => Results.Text(context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));

await app.StartAsync();

string? serverAddress = null;
foreach (string appUrl in app.Urls)
{
    if (serverAddress is not null)
    {
        throw new InvalidOperationException("The sample expected a single bound server address.");
    }

    serverAddress = appUrl;
}

if (serverAddress is null)
{
    throw new InvalidOperationException("The sample could not resolve the bound server address.");
}

EgressPoolOptions options = new()
{
    Prefixes = [IPNetwork.Parse("127.0.0.0/8")],
    AddressMode = EgressAddressMode.NonLocalBind,
    InterfaceSelectionMode = EgressInterfaceSelectionMode.Explicit,
    InterfaceName = "lo",
    ManageLocalRoutes = false,
};

await using EgressPool pool = await EgressPool.CreateAsync(options);

for (int requestIndex = 0; requestIndex < 8; requestIndex++)
{
    using HttpClient httpClient = pool.CreateHttpClient();
    string remoteAddress = await httpClient.GetStringAsync(serverAddress);
    Console.WriteLine(remoteAddress);
}

await app.StopAsync();
