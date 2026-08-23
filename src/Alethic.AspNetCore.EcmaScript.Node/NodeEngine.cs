using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.JavaScript.NodeApi;
using Microsoft.JavaScript.NodeApi.Runtime;

namespace Alethic.AspNetCore.EcmaScript.Node;

/// <summary>
/// One embedded Node runtime on its own thread, and the modules evaluated on it.
/// </summary>
sealed class NodeEngine : IAsyncDisposable
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
	/// Promotes the runtime's <c>require</c> to a global, and defuses unhandled rejections.
	/// </summary>
	/// <remarks>
	/// The main script is evaluated as CommonJS, so <c>require</c> is in scope here and nowhere else.
	/// It reaches built-in modules only, which is all a self-contained bundle needs, and is why the
	/// runtime is created without a base directory: the module bootstrap that would otherwise run
	/// expects a <c>require</c> able to read from disk and fails when it does not find one.
	///
	/// The rejection handler is not optional. A promise rejection with no handler yet attached — a
	/// gap that async .NET callers cannot always avoid — is fatal by Node's default, and here fatal
	/// means the whole process. The rejection still reaches whoever awaits the promise; this only
	/// stops the gap itself from killing anything.
	/// </remarks>
	const string MainScript =
		"globalThis.require = require;\n" +
		"process.on('unhandledRejection', (reason) => {\n" +
		"    console.error('[Alethic.AspNetCore.EcmaScript] unhandled promise rejection:', reason);\n" +
		"});\n";

	readonly NodeEmbeddingThreadRuntime runtime;
	readonly ILogger logger;
	readonly ConcurrentDictionary<string, Lazy<Task<JSReference>>> modules = new();

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
			// aborts the process from native code rather than raising anything catchable. Nothing here
			// needs it.
			RuntimeFlags = NodejsRuntime.NodeEmbeddingRuntimeFlags.NoCreateInspector |
				NodejsRuntime.NodeEmbeddingRuntimeFlags.NoStartDebugSignalHandler,
		});

		runtime.Run(() => JSValue.RunScript(LoaderScript));
	}

	/// <summary>
	/// The number of leases currently held against this engine.
	/// </summary>
	internal int InFlight;

	/// <summary>
	/// The underlying runtime.
	/// </summary>
	internal NodeEmbeddingThreadRuntime Runtime => runtime;

	/// <summary>
	/// Evaluates a module on this engine, or returns the exports already evaluated for this source.
	/// </summary>
	/// <remarks>
	/// Lazy, so that concurrent callers asking for the same module evaluate it once — evaluation
	/// occupies this engine's event loop, and doing it twice would stall it twice. The reference must
	/// be dereferenced on the engine's thread.
	/// </remarks>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	public Task<JSReference> ImportAsync(NodeModuleSource source, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		ObjectDisposedException.ThrowIf(disposed, this);

		return modules.GetOrAdd(source.Key, _ => new Lazy<Task<JSReference>>(
			() => EvaluateAsync(source, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication)).Value;
	}

	/// <summary>
	/// Reads and evaluates a module on this engine's thread.
	/// </summary>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	async Task<JSReference> EvaluateAsync(NodeModuleSource source, CancellationToken cancellationToken)
	{
		var text = await source.ReadAsync(cancellationToken);
		logger.LogDebug("Evaluating module {Module} ({Length} bytes).", source.Name, text.Length);

		return runtime.Run(() =>
		{
			var exports = JSValue.Global["__alethicLoad"].Call(JSValue.Undefined, text, source.Name);
			return new JSReference(exports, isWeak: false);
		});
	}

	/// <summary>
	/// Posts work to the engine's thread, quietly dropping it if the engine is gone.
	/// </summary>
	/// <remarks>
	/// The posted delegate swallows everything, deliberately. A queued callback that throws while
	/// the runtime is being deleted recurses inside the native callback's exception dispatch and
	/// takes the process down with a stack overflow — and the only work posted this way is cleanup,
	/// whose failure means the engine is already tearing the world down anyway.
	/// </remarks>
	/// <param name="action"></param>
	public void TryPost(Action action)
	{
		if (disposed)
			return;

		try
		{
			runtime.Post(() =>
			{
				try
				{
					action();
				}
				catch
				{
				}
			}, allowSync: false);
		}
		catch (ObjectDisposedException)
		{
			// A disposed engine has torn the whole world down already; there is nothing left the
			// posted work could have affected.
		}
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
