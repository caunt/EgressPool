using Egress;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds egress pool integration to <see cref="IHttpClientBuilder" /> instances.
/// </summary>
public static class EgressPoolHttpClientBuilderExtensions
{
    /// <summary>
    /// Configures the HTTP client to create new connections through the registered <see cref="EgressPool" />.
    /// </summary>
    /// <param name="builder">The HTTP client builder.</param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder UseEgressPool(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            serviceProvider.GetRequiredService<EgressPool>().CreateHttpMessageHandler());
    }
}
