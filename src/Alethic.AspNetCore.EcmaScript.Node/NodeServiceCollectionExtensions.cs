using System;

using Alethic.AspNetCore.EcmaScript;
using Alethic.AspNetCore.EcmaScript.Node;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the two Node pieces: the engine pool, and rendering engines on it.
/// </summary>
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
				p.GetRequiredService<ILoggerFactory>()));
		else
			services.AddKeyedSingleton(serviceKey, (p, _) => new NodeEnginePool(
				p.GetRequiredService<IOptionsMonitor<NodeEnginePoolOptions>>().Get(optionsName),
				p.GetRequiredService<ILoggerFactory>()));

		return services;
	}

	/// <summary>
	/// Registers a rendering engine on a Node engine pool.
	/// </summary>
	/// <remarks>
	/// The engine registers as <see cref="IRenderEngine"/> — the abstraction the endpoint layer
	/// consumes — and refers to a pool registered separately with
	/// <see cref="AddNodeEnginePool(IServiceCollection, Action{NodeEnginePoolOptions})"/>.
	/// </remarks>
	/// <param name="services"></param>
	/// <param name="configure"></param>
	public static IServiceCollection AddNodeRenderEngine(this IServiceCollection services, Action<NodeRenderEngineOptions> configure) =>
		services.AddNodeRenderEngine(null, configure);

	/// <summary>
	/// Registers a keyed rendering engine on a Node engine pool.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="serviceKey"></param>
	/// <param name="configure"></param>
	public static IServiceCollection AddNodeRenderEngine(this IServiceCollection services, object? serviceKey, Action<NodeRenderEngineOptions> configure)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configure);

		services.AddOptions();
		services.AddLogging();

		var optionsName = serviceKey?.ToString() ?? "";
		services.Configure(optionsName, configure);

		if (serviceKey is null)
			services.AddSingleton<IRenderEngine>(p => Create(p, optionsName));
		else
			services.AddKeyedSingleton<IRenderEngine>(serviceKey, (p, _) => Create(p, optionsName));

		return services;
	}

	/// <summary>
	/// Builds a rendering engine from its named options and the pool those options name.
	/// </summary>
	/// <param name="provider"></param>
	/// <param name="optionsName"></param>
	static NodeRenderEngine Create(IServiceProvider provider, string optionsName)
	{
		var options = provider.GetRequiredService<IOptionsMonitor<NodeRenderEngineOptions>>().Get(optionsName);

		var pool = options.PoolKey is null
			? provider.GetService<NodeEnginePool>()
			: provider.GetKeyedService<NodeEnginePool>(options.PoolKey);

		if (pool is null)
			throw new InvalidOperationException("No Node engine pool is registered. Add one with AddNodeEnginePool().");

		return new NodeRenderEngine(pool, options, provider.GetRequiredService<ILogger<NodeRenderEngine>>());
	}

}
