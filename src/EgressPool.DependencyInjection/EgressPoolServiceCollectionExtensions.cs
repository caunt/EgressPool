using Egress;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    /// <param name="configureOptions">The options configuration callback. When <see langword="null" />, default options are used.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddEgressPool(this IServiceCollection services, Action<EgressPoolOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<EgressPoolOptions> optionsBuilder = services.AddOptions<EgressPoolOptions>();
        if (configureOptions is not null)
        {
            optionsBuilder.Configure(configureOptions);
        }
        services.TryAddSingleton(serviceProvider =>
        {
            EgressPoolOptions options = serviceProvider.GetRequiredService<IOptions<EgressPoolOptions>>().Value;
            ILogger<EgressPool>? logger = serviceProvider.GetService<ILogger<EgressPool>>();
            return EgressPool.CreateAsync(options, logger).AsTask().GetAwaiter().GetResult();
        });

        return services;
    }
}
