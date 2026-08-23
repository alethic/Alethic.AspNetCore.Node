using System;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.JavaScript.NodeApi;

namespace Alethic.EcmaScript.Hosting.Node;

/// <summary>
/// A module evaluated on one embedded Node engine.
/// </summary>
/// <remarks>
/// Everything here obeys one rule: a <see cref="JSValue"/> is valid only inside the scope that
/// produced it, and awaiting ends that scope even without leaving the thread. Anything needed after
/// an await is therefore held by <see cref="JSReference"/> and re-read, and anything handed back to
/// a caller has already become .NET data.
/// </remarks>
sealed class NodeModuleInstance : IJavaScriptModuleInstance
{

	readonly NodeEngine engine;
	readonly JavaScriptModuleSource source;
	readonly JSReference exports;
	readonly ILogger logger;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="engine"></param>
	/// <param name="source"></param>
	/// <param name="exports"></param>
	/// <param name="logger"></param>
	public NodeModuleInstance(NodeEngine engine, JavaScriptModuleSource source, JSReference exports, ILogger logger)
	{
		this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
		this.source = source ?? throw new ArgumentNullException(nameof(source));
		this.exports = exports ?? throw new ArgumentNullException(nameof(exports));
		this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc />
	public JavaScriptModuleSource Source => source;

	/// <inheritdoc />
	public async Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(export);
		ArgumentNullException.ThrowIfNull(arguments);

		var json = JsonSerializer.Serialize(arguments);

		var result = await engine.Runtime.RunAsync(async () =>
		{
			var (target, function) = Resolve(export);
			var args = ToArray(JSValue.Global["JSON"].CallMethod("parse", json));
			var value = function.Call(target, args);

			// An export may answer synchronously or with a promise; normalizing through the runtime's
			// own Promise.resolve accepts both without caring which.
			value = await ((JSPromise)JSValue.Global["Promise"].CallMethod("resolve", value)).AsTask();

			return value.IsUndefined() || value.IsNull()
				? null
				: (string?)JSValue.Global["JSON"].CallMethod("stringify", value);
		});

		return result is null ? default : JsonSerializer.Deserialize<T>(result);
	}

	/// <inheritdoc />
	public async Task<JavaScriptStreamResponse> InvokeStreamAsync(string export, object?[] arguments, ReadOnlyMemory<byte>? payload, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(export);
		ArgumentNullException.ThrowIfNull(arguments);

		var pipe = new Pipe();
		var head = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

		// The call outlives this method: the response is returned once its head is known, and its
		// body continues to arrive afterwards. Faults before the head surface to the caller; faults
		// after it can only truncate the body, the head having already been delivered.
		var pump = PumpAsync(export, JsonSerializer.Serialize(arguments), payload, pipe.Writer, head, cancellationToken);

		var completed = await Task.WhenAny(head.Task, pump);
		if (completed == pump)
			await pump; // faulted before producing a head; observe the exception

		return new JavaScriptStreamResponse(await head.Task, pipe.Reader.AsStream());
	}

	/// <summary>
	/// Invokes the export and drains its body into the pipe.
	/// </summary>
	/// <param name="export"></param>
	/// <param name="argumentsJson"></param>
	/// <param name="payload"></param>
	/// <param name="writer"></param>
	/// <param name="head"></param>
	/// <param name="cancellationToken"></param>
	async Task PumpAsync(string export, string argumentsJson, ReadOnlyMemory<byte>? payload, PipeWriter writer, TaskCompletionSource<string?> head, CancellationToken cancellationToken)
	{
		try
		{
			await engine.Runtime.RunAsync(async () =>
			{
				// The signal belongs to the runtime, but cancellation arrives on some other thread,
				// so the controller is held by reference and the abort posted back here.
				var controller = JSValue.RunScript("new AbortController()");
				using var controllerRef = new JSReference(controller, isWeak: false);
				using var registration = cancellationToken.Register(static state =>
				{
					var (rt, reference) = ((NodeEngine, JSReference))state!;
					rt.Runtime.Post(() => reference.GetValue().CallMethod("abort", "the invocation was cancelled"), allowSync: false);
				}, (engine, controllerRef));

				var (target, function) = Resolve(export);
				var args = ToArray(JSValue.Global["JSON"].CallMethod("parse", argumentsJson));

				// The streaming call convention: the JSON arguments, then the payload, then the signal.
				var full = new JSValue[args.Length + 2];
				args.CopyTo(full, 0);
				full[^2] = payload is { } bytes ? new JSTypedArray<byte>(bytes.ToArray()) : JSValue.Null;
				full[^1] = controller["signal"];

				var value = function.Call(target, full);

				// Scope ends at the await; everything above is invalid from the next line on.
				var result = await ((JSPromise)JSValue.Global["Promise"].CallMethod("resolve", value)).AsTask();

				if (result.IsObject() == false)
					throw new InvalidOperationException($"Streaming export '{export}' of module '{source.Name}' did not produce an object of the form {{ head, body }}.");

				var headValue = result["head"];
				head.TrySetResult(headValue.IsUndefined() || headValue.IsNull()
					? null
					: (string?)JSValue.Global["JSON"].CallMethod("stringify", headValue));

				var body = result["body"];
				if (body.IsNull() || body.IsUndefined())
					return 0;

				using var reader = new JSReference(body.CallMethod("getReader"), isWeak: false);

				while (true)
				{
					var read = (JSPromise)reader.GetValue().CallMethod("read");
					var chunk = await read.AsTask();
					if ((bool)chunk["done"])
						break;

					// Copied into .NET memory while still inside the scope that produced it.
					var copied = ((JSTypedArray<byte>)chunk["value"]).Span.ToArray();
					var flushed = await writer.WriteAsync(copied, CancellationToken.None);
					if (flushed.IsCompleted)
						break; // the consumer gave up on the body
				}

				return 0;
			});

			await writer.CompleteAsync();
		}
		catch (Exception e)
		{
			logger.LogDebug(e, "Streaming export {Export} of module {Module} failed.", export, source.Name);
			head.TrySetException(e);
			await writer.CompleteAsync(e);
		}
	}

	/// <summary>
	/// Resolves a dotted export path to the function and the object it hangs from, so the call keeps
	/// the <c>this</c> the module expects. Must be called on the runtime's thread.
	/// </summary>
	/// <param name="export"></param>
	/// <exception cref="InvalidOperationException"></exception>
	(JSValue Target, JSValue Function) Resolve(string export)
	{
		var target = exports.GetValue();
		var segments = export.Split('.');

		for (var i = 0; i < segments.Length - 1; i++)
		{
			target = target[segments[i]];
			if (target.IsUndefined() || target.IsNull())
				throw new InvalidOperationException($"Module '{source.Name}' has no export path '{export}'.");
		}

		var function = target[segments[^1]];
		if (function.IsFunction() == false)
			throw new InvalidOperationException($"Module '{source.Name}' has no function at export path '{export}'.");

		return (target, function);
	}

	/// <summary>
	/// Spreads a runtime array into arguments. Must be called on the runtime's thread.
	/// </summary>
	/// <param name="array"></param>
	static JSValue[] ToArray(JSValue array)
	{
		var length = (int)array["length"];
		var values = new JSValue[length];
		for (var i = 0; i < length; i++)
			values[i] = array[i];

		return values;
	}

}
