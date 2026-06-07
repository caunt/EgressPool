using Egress;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds egress pool services to an <see cref="IServiceCollection" />.
/// </summary>
public static class EgressPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="EgressPool" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">The options configuration callback.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddEgressPool(this IServiceCollection services, Action<EgressPoolOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddOptions<EgressPoolOptions>().Configure(configureOptions);
        services.TryAddSingleton(serviceProvider =>
        {
            EgressPoolOptions options = serviceProvider.GetRequiredService<IOptions<EgressPoolOptions>>().Value;
            return EgressPool.CreateAsync(options).AsTask().GetAwaiter().GetResult();
        });

        return services;
    }
}
