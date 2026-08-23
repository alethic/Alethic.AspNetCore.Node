using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Resolves pools by name, creating each on first use.
/// </summary>
public interface IJavaScriptEnginePoolProvider
{

	/// <summary>
	/// Returns the pool registered under the given name.
	/// </summary>
	/// <param name="name"></param>
	IJavaScriptEnginePool Get(string name);

}

/// <summary>
/// Default provider, backed by named options and a registered engine provider.
/// </summary>
sealed class JavaScriptEnginePoolProvider : IJavaScriptEnginePoolProvider, IAsyncDisposable
{

	/// <summary>
	/// Name used when a pool is registered without one.
	/// </summary>
	public const string DefaultName = "Default";

	readonly IServiceProvider services;
	readonly IOptionsMonitor<JavaScriptEnginePoolOptions> options;
	readonly ILoggerFactory loggerFactory;
	readonly ConcurrentDictionary<string, Lazy<IJavaScriptEnginePool>> pools = new();

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="options"></param>
	/// <param name="loggerFactory"></param>
	public JavaScriptEnginePoolProvider(IServiceProvider services, IOptionsMonitor<JavaScriptEnginePoolOptions> options, ILoggerFactory loggerFactory)
	{
		this.services = services ?? throw new ArgumentNullException(nameof(services));
		this.options = options ?? throw new ArgumentNullException(nameof(options));
		this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
	}

	/// <inheritdoc />
	public IJavaScriptEnginePool Get(string name)
	{
		ArgumentNullException.ThrowIfNull(name);

		return pools.GetOrAdd(name, n => new Lazy<IJavaScriptEnginePool>(() => Create(n), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
	}

	/// <summary>
	/// Builds a pool from its named options.
	/// </summary>
	/// <param name="name"></param>
	IJavaScriptEnginePool Create(string name)
	{
		var provider = services.GetKeyedService<IJavaScriptEngineProvider>(name)
			?? services.GetService<IJavaScriptEngineProvider>()
			?? throw new InvalidOperationException($"No JavaScript engine provider is registered for pool '{name}'. Add one, for example with UseEmbeddedNode().");

		return new JavaScriptEnginePool(name, provider, options.Get(name), loggerFactory.CreateLogger<JavaScriptEnginePool>());
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		foreach (var pool in pools.Values)
			if (pool.IsValueCreated)
				await pool.Value.DisposeAsync();

		pools.Clear();
	}

}
