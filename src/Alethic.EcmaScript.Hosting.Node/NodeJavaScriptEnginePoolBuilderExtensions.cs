using System;

using Alethic.EcmaScript.Hosting;
using Alethic.EcmaScript.Hosting.Node;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Attaches the embedded Node backend to a pool.
/// </summary>
public static class NodeJavaScriptEnginePoolBuilderExtensions
{

	/// <summary>
	/// Runs this pool's engines on a Node runtime embedded in the process.
	/// </summary>
	/// <remarks>
	/// The native library is located beside the application or under its runtime identifier. Reference
	/// the runtime-specific <c>Microsoft.JavaScript.LibNode</c> package rather than the umbrella one,
	/// which depends on every platform at once and lands all of them in the build output.
	/// </remarks>
	/// <param name="builder"></param>
	/// <param name="configure"></param>
	public static JavaScriptEnginePoolBuilder UseEmbeddedNode(this JavaScriptEnginePoolBuilder builder, Action<JavaScriptEnginePoolOptions>? configure = null)
	{
		ArgumentNullException.ThrowIfNull(builder);

		if (configure is not null)
			builder.Services.Configure(builder.Name, configure);

		// Keyed by pool name so pools may run on different backends within one application.
		builder.Services.TryAddKeyedSingleton<IJavaScriptEngineProvider, NodeEngineProvider>(builder.Name);
		builder.Services.TryAddSingleton<IJavaScriptEngineProvider, NodeEngineProvider>();
		return builder;
	}

}
