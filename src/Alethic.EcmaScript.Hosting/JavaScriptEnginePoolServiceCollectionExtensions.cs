using System;

using Alethic.EcmaScript.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers JavaScript engine pools.
/// </summary>
public static class JavaScriptEnginePoolServiceCollectionExtensions
{

	/// <summary>
	/// Registers the default pool.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="configure"></param>
	public static JavaScriptEnginePoolBuilder AddJavaScriptEnginePool(this IServiceCollection services, Action<JavaScriptEnginePoolOptions>? configure = null) =>
		services.AddJavaScriptEnginePool(JavaScriptEnginePoolProvider.DefaultName, configure);

	/// <summary>
	/// Registers a named pool.
	/// </summary>
	/// <remarks>
	/// Nothing is started here. A pool stands its engines up as demand requires them, up to the
	/// configured size, so an application that never dispatches never pays for a runtime.
	/// </remarks>
	/// <param name="services"></param>
	/// <param name="name"></param>
	/// <param name="configure"></param>
	public static JavaScriptEnginePoolBuilder AddJavaScriptEnginePool(this IServiceCollection services, string name, Action<JavaScriptEnginePoolOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(name);

		services.AddOptions();
		services.AddLogging();

		if (configure is not null)
			services.Configure(name, configure);

		services.TryAddSingletonPoolProvider();
		return new JavaScriptEnginePoolBuilder(services, name);
	}

	/// <summary>
	/// Adds the pool provider once, however many pools are registered.
	/// </summary>
	/// <param name="services"></param>
	static void TryAddSingletonPoolProvider(this IServiceCollection services)
	{
		foreach (var descriptor in services)
			if (descriptor.ServiceType == typeof(IJavaScriptEnginePoolProvider))
				return;

		services.AddSingleton<IJavaScriptEnginePoolProvider, JavaScriptEnginePoolProvider>();
	}

}
