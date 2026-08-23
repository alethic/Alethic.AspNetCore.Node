using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Alethic.EcmaScript.Hosting.Node;

/// <summary>
/// Creates engines backed by a Node runtime embedded in this process.
/// </summary>
public sealed class NodeEngineProvider : IJavaScriptEngineProvider
{

	readonly ILoggerFactory loggerFactory;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="loggerFactory"></param>
	public NodeEngineProvider(ILoggerFactory loggerFactory)
	{
		this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
	}

	/// <inheritdoc />
	public ValueTask<IJavaScriptEngine> CreateAsync(JavaScriptEnginePoolOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);

		var platform = NodeRuntimeHost.GetOrCreate(LibNodeLocator.Locate(options.RuntimePath));
		var logger = loggerFactory.CreateLogger<NodeEngine>();

		// Starting a runtime is synchronous and takes a couple of hundred milliseconds, so it is kept
		// off whichever thread happened to ask for it.
		return new ValueTask<IJavaScriptEngine>(Task.Run<IJavaScriptEngine>(() => new NodeEngine(platform, logger), cancellationToken));
	}

}
