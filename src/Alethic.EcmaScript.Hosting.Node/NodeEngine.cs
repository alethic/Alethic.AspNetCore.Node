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
	/// Promotes the runtime's <c>require</c> to a global, and defuses unhandled rejections.
	/// </summary>
	/// <remarks>
	/// The main script is evaluated as CommonJS, so <c>require</c> is in scope here and nowhere else.
	/// It reaches built-in modules only, which is all a self-contained bundle needs, and is why the
	/// runtime is created without a base directory: the module bootstrap that would otherwise run
	/// expects a <c>require</c> able to read from disk and fails when it does not find one.
	///
	/// The rejection handler is not optional. The object model hands promises to .NET as handles,
	/// and a handle-holder attaches to the promise a moment after it was created — so a rejection
	/// landing in that gap counts as unhandled, and Node's default response is to kill the runtime,
	/// which here is the whole process. The rejection still reaches whoever awaits the handle; this
	/// only stops the gap itself from being fatal.
	/// </remarks>
	const string MainScript =
		"globalThis.require = require;\n" +
		"process.on('unhandledRejection', (reason) => {\n" +
		"    console.error('[Alethic.EcmaScript] unhandled promise rejection:', reason);\n" +
		"});\n";

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
			// aborts the process from native code rather than raising anything catchable. Nothing here
			// needs it.
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
	public ValueTask<IJavaScriptModuleInstance> ImportAsync(JavaScriptModuleSource source, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(source);
		ObjectDisposedException.ThrowIf(disposed, this);

		// Lazy, so that concurrent callers asking for the same module evaluate it once. Evaluation
		// occupies this engine's event loop, so doing it twice would stall it twice.
		var lazy = modules.GetOrAdd(source.Key, _ => new Lazy<Task<IJavaScriptModuleInstance>>(
			() => EvaluateModuleAsync(source, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication));

		return new ValueTask<IJavaScriptModuleInstance>(lazy.Value);
	}

	/// <inheritdoc />
	public Task<JavaScriptValue> EvaluateAsync(string script, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(script);
		ObjectDisposedException.ThrowIf(disposed, this);

		return runtime.RunAsync(() => Task.FromResult(Convert(JSValue.RunScript(script))));
	}

	/// <inheritdoc />
	public Task<JavaScriptValue> CreateByteArrayAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(disposed, this);

		var copy = bytes.ToArray();
		return runtime.RunAsync(() => Task.FromResult(Convert(new JSTypedArray<byte>(copy))));
	}

	/// <summary>
	/// Converts an engine value to its .NET representation: primitives by value, structure as a
	/// handle. Must be called on the engine's thread.
	/// </summary>
	/// <param name="value"></param>
	internal JavaScriptValue Convert(JSValue value) => value.TypeOf() switch
	{
		JSValueType.Undefined => JavaScriptValue.Undefined,
		JSValueType.Null => JavaScriptValue.Null,
		JSValueType.Boolean => (bool)value,
		JSValueType.Number => (double)value,
		JSValueType.String => (string)value,
		JSValueType.Object or JSValueType.Function => JavaScriptValue.From(new NodeJavaScriptObject(this, new JSReference(value, isWeak: false))),
		var other => throw new NotSupportedException($"JavaScript values of type {other} have no .NET representation."),
	};

	/// <summary>
	/// Converts a .NET representation back to an engine value. Must be called on the engine's thread.
	/// </summary>
	/// <param name="value"></param>
	/// <exception cref="InvalidOperationException"></exception>
	internal JSValue ConvertBack(JavaScriptValue value) => value.Kind switch
	{
		JavaScriptValueKind.Undefined => JSValue.Undefined,
		JavaScriptValueKind.Null => JSValue.Null,
		JavaScriptValueKind.Boolean => value.AsBoolean(),
		JavaScriptValueKind.Number => value.AsNumber(),
		JavaScriptValueKind.String => value.AsString(),
		_ => value.AsObject() is NodeJavaScriptObject handle && ReferenceEquals(handle.NodeEngine, this)
			? handle.Value
			: throw new InvalidOperationException("The object handle belongs to a different engine and cannot be passed to this one."),
	};

	/// <summary>
	/// Converts several values back at once. Must be called on the engine's thread.
	/// </summary>
	/// <param name="values"></param>
	internal JSValue[] ConvertBack(JavaScriptValue[] values)
	{
		var converted = new JSValue[values.Length];
		for (var i = 0; i < values.Length; i++)
			converted[i] = ConvertBack(values[i]);

		return converted;
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
	internal void TryPost(Action action)
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

	/// <summary>
	/// Reads and evaluates a module on this engine's thread.
	/// </summary>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	async Task<IJavaScriptModuleInstance> EvaluateModuleAsync(JavaScriptModuleSource source, CancellationToken cancellationToken)
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

		return new NodeModuleInstance(this, source, exports);
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
