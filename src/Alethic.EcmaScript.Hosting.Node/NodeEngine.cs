using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.JavaScript.NodeApi;
using Microsoft.JavaScript.NodeApi.Runtime;

namespace Alethic.EcmaScript.Hosting.Node;

/// <summary>
/// An engine backed by a Node runtime embedded in this process.
/// </summary>
sealed class NodeEngine : IJavaScriptEngine
{

	/// <summary>
	/// Installs the module loader into a fresh runtime.
	/// </summary>
	/// <remarks>
	/// This is the CommonJS wrapper Node's own loader applies, reproduced because the embedded
	/// runtime does not expose one that reads from disk. Wrapping rather than evaluating at global
	/// scope is what keeps a module's declarations out of the shared global object, and the appended
	/// source URL is what keeps its frames legible in a stack trace.
	/// </remarks>
	const string LoaderScript = """
		globalThis.__alethicLoad = (source, filename) => {
			const module = { exports: {} };
			const fn = new Function('module', 'exports', 'require', '__filename',
				source + '\n//# sourceURL=' + filename);
			fn(module, module.exports, require, filename);
			return module.exports;
		};
		""";

	/// <summary>
	/// Promotes the runtime's <c>require</c> to a global.
	/// </summary>
	/// <remarks>
	/// The main script is evaluated as CommonJS, so <c>require</c> is in scope here and nowhere else.
	/// It reaches built-in modules only, which is all a self-contained bundle needs, and is why the
	/// runtime is created without a base directory: the module bootstrap that would otherwise run
	/// expects a <c>require</c> able to read from disk and fails when it does not find one.
	/// </remarks>
	const string MainScript = "globalThis.require = require;";

	readonly NodeEmbeddingThreadRuntime runtime;
	readonly ILogger logger;
	readonly ConcurrentDictionary<string, Lazy<Task<IJavaScriptModuleInstance>>> modules = new();

	bool disposed;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="platform"></param>
	/// <param name="logger"></param>
	public NodeEngine(NodeEmbeddingPlatform platform, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(platform);
		this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

		runtime = platform.CreateThreadRuntime(null, new NodeEmbeddingRuntimeSettings()
		{
			MainScript = MainScript,

			// The inspector agent may only start once per process, and a second runtime attempting it
			// aborts the process from native code rather than raising anything catchable. Rendering
			// never needs it.
			RuntimeFlags = NodejsRuntime.NodeEmbeddingRuntimeFlags.NoCreateInspector |
				NodejsRuntime.NodeEmbeddingRuntimeFlags.NoStartDebugSignalHandler,
		});

		runtime.Run(() => JSValue.RunScript(LoaderScript));
	}

	/// <summary>
	/// The underlying runtime, for members that must marshal onto its thread.
	/// </summary>
	internal NodeEmbeddingThreadRuntime Runtime => runtime;

	/// <inheritdoc />
	public ValueTask<IJavaScriptModuleInstance> ImportAsync(JavaScriptModuleSource source, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		ObjectDisposedException.ThrowIf(disposed, this);

		// Lazy, so that concurrent callers asking for the same module evaluate it once. Evaluation
		// occupies this engine's event loop, so doing it twice would stall it twice.
		var lazy = modules.GetOrAdd(source.Key, _ => new Lazy<Task<IJavaScriptModuleInstance>>(
			() => EvaluateAsync(source, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication));

		return new ValueTask<IJavaScriptModuleInstance>(lazy.Value);
	}

	/// <summary>
	/// Reads and evaluates a module on this engine's thread.
	/// </summary>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	async Task<IJavaScriptModuleInstance> EvaluateAsync(JavaScriptModuleSource source, CancellationToken cancellationToken)
	{
		var text = await source.ReadAsync(cancellationToken);
		logger.LogDebug("Evaluating JavaScript module {Module} ({Length} bytes).", source.Name, text.Length);

		var exports = runtime.Run(() =>
		{
			// The whole exports object, unjudged: which exports a module must carry is its consumers'
			// contract, not this layer's.
			var value = JSValue.Global["__alethicLoad"].Call(JSValue.Undefined, text, source.Name);
			return new JSReference(value, isWeak: false);
		});

		return new NodeModuleInstance(this, source, exports, logger);
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		if (disposed)
			return ValueTask.CompletedTask;

		disposed = true;
		runtime.Dispose();
		return ValueTask.CompletedTask;
	}

}
