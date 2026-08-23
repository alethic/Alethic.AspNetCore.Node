using System;

using Microsoft.Extensions.DependencyInjection;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Names a pool under construction, so a backend can be attached to it.
/// </summary>
public sealed class JavaScriptEnginePoolBuilder
{

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="name"></param>
	internal JavaScriptEnginePoolBuilder(IServiceCollection services, string name)
	{
		Services = services ?? throw new ArgumentNullException(nameof(services));
		Name = name ?? throw new ArgumentNullException(nameof(name));
	}

	/// <summary>
	/// The collection this pool is being registered into.
	/// </summary>
	public IServiceCollection Services { get; }

	/// <summary>
	/// The name this pool is registered under.
	/// </summary>
	public string Name { get; }

}
