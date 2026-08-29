using System;

using Alethic.AspNetCore.Node;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the engine pool.
/// </summary>
/// <remarks>
/// The pool is the only piece worth a registration: it owns the engine threads, and everything else
/// in the application shares one. A request handler is a small object over a pool and is constructed
/// where it is mounted, so nothing registers it; the modules are Node's own, cached per engine in
/// <c>require.cache</c>.
/// </remarks>
public static class NodeServiceCollectionExtensions
{

    /// <summary>
    /// Registers a pool of embedded Node engines.
    /// </summary>
    /// <remarks>
    /// The pool is a concrete facility, resolvable as <see cref="NodeEnginePool"/> and usable for
    /// any JavaScript work, web or otherwise. Nothing is started here; engines stand up as demand
    /// requires them, or when something prepares the pool ahead of traffic.
    /// </remarks>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    public static IServiceCollection AddNodeEnginePool(this IServiceCollection services, Action<NodeEnginePoolOptions>? configure = null) =>
        services.AddNodeEnginePool(null, configure);

    /// <summary>
    /// Registers a keyed pool of embedded Node engines.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="serviceKey"></param>
    /// <param name="configure"></param>
    public static IServiceCollection AddNodeEnginePool(this IServiceCollection services, object? serviceKey, Action<NodeEnginePoolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        services.AddLogging();

        var optionsName = serviceKey?.ToString() ?? "";
        if (configure is not null)
            services.Configure(optionsName, configure);

        if (serviceKey is null)
            services.AddSingleton(p => new NodeEnginePool(
                p.GetRequiredService<IOptionsMonitor<NodeEnginePoolOptions>>().Get(optionsName),
                p.GetRequiredService<ILoggerFactory>(),
                p));
        else
            services.AddKeyedSingleton(serviceKey, (p, _) => new NodeEnginePool(
                p.GetRequiredService<IOptionsMonitor<NodeEnginePoolOptions>>().Get(optionsName),
                p.GetRequiredService<ILoggerFactory>(),
                p));

        return services;
    }

}
