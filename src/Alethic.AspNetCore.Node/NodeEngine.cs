using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.JavaScript.NodeApi;
using Microsoft.JavaScript.NodeApi.Runtime;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// One embedded Node runtime on its own thread.
/// </summary>
sealed class NodeEngine : IAsyncDisposable
{

	/// <summary>
	/// Gives the runtime a <c>require</c> that resolves files, and defuses unhandled rejections.
	/// </summary>
	/// <remarks>
	/// <c>module.createRequire</c> is the documented way to get a working loader in an embedded
	/// runtime, and it is Node's own: it resolves paths and packages by Node's rules and caches what
	/// it loads in <c>require.cache</c>, keyed by resolved filename. That is the whole of this
	/// library's module handling — a module gets one instance per runtime, with its module scope
	/// intact, because that is what <c>require</c> means.
	///
	/// The rejection handler is not optional. A promise rejection with no handler yet attached — a
	/// gap that async .NET callers cannot always avoid — is fatal by Node's default, and here fatal
	/// means the whole process. The rejection still reaches whoever awaits the promise; this only
	/// stops the gap itself from killing anything.
	/// </remarks>
	const string MainScript =
		"globalThis.require = require('module').createRequire(process.execPath);\n" +
		"process.on('unhandledRejection', (reason) => {\n" +
		"    console.error('[Alethic.AspNetCore.Node] unhandled promise rejection:', reason);\n" +
		"});\n";

	readonly NodeEmbeddingThreadRuntime runtime;
	readonly ILogger logger;

	bool disposed;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="platform"></param>
	/// <param name="baseDirectory">Root for Node's package resolution.</param>
	/// <param name="logger"></param>
	public NodeEngine(NodeEmbeddingPlatform platform, string baseDirectory, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(platform);
		ArgumentNullException.ThrowIfNull(baseDirectory);
		this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

		runtime = platform.CreateThreadRuntime(baseDirectory, new NodeEmbeddingRuntimeSettings()
		{
			MainScript = MainScript,

			// The inspector agent may only start once per process, and a second runtime attempting it
			// aborts the process from native code rather than raising anything catchable. Nothing here
			// needs it.
			RuntimeFlags = NodejsRuntime.NodeEmbeddingRuntimeFlags.NoCreateInspector |
				NodejsRuntime.NodeEmbeddingRuntimeFlags.NoStartDebugSignalHandler,
		});
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
	/// Runs work against a module's exports on this engine's thread, loading the module first if
	/// Node has not already.
	/// </summary>
	/// <remarks>
	/// Nothing is cached here, and nothing is referenced. <c>require</c> caches by resolved filename
	/// in <c>require.cache</c>, which is exactly the identity a module has in any other Node program
	/// — one instance per runtime, module scope intact, evaluated on first use — so the exports are
	/// fetched inside the same trip onto the thread that uses them. Handing a
	/// <see cref="JSReference"/> back instead would mean a strong reference per call, kept alive
	/// against a module the runtime is already keeping alive, and nothing to dispose it.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="source"></param>
	/// <param name="work"></param>
	/// <param name="cancellationToken"></param>
	public async Task<T> RunAsync<T>(NodeModuleSource source, Func<JSValue, Task<T>> work, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(work);
		ObjectDisposedException.ThrowIf(disposed, this);

		var path = await ResolveAsync(source, cancellationToken);
		return await runtime.RunAsync(() => work(Require(path)));
	}

	/// <summary>
	/// Runs synchronous work against a module's exports on this engine's thread.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="source"></param>
	/// <param name="work"></param>
	public T Run<T>(NodeModuleSource source, Func<JSValue, T> work)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(work);
		ObjectDisposedException.ThrowIf(disposed, this);

		var path = ResolveAsync(source, CancellationToken.None).GetAwaiter().GetResult();
		return runtime.Run(() => work(Require(path)));
	}

	/// <summary>
	/// Loads a module on this engine ahead of use, so that evaluating it does not land under the
	/// first call that needs it.
	/// </summary>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	public async Task ImportAsync(NodeModuleSource source, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		ObjectDisposedException.ThrowIf(disposed, this);

		var path = await ResolveAsync(source, cancellationToken);
		runtime.Run(() => Require(path).IsObject());
	}

	/// <summary>
	/// Resolves a module to its path, logging what is about to be required.
	/// </summary>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	async Task<string> ResolveAsync(NodeModuleSource source, CancellationToken cancellationToken)
	{
		var path = await source.ResolveAsync(cancellationToken);
		logger.LogDebug("Requiring module {Module} from {Path}.", source.Name, path);

		return path;
	}

	/// <summary>
	/// Node's own loader. Must be called on the engine's thread.
	/// </summary>
	/// <param name="path"></param>
	static JSValue Require(string path) => JSValue.Global["require"].Call(JSValue.Undefined, path);

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
