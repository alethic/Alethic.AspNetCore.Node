using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JavaScript.NodeApi;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Renders by calling the application's <c>fetch</c> handler, on a Node engine pool.
/// </summary>
/// <remarks>
/// The stock <see cref="INodeRequestHandler"/>, and the one most applications need. The handler is called
/// as <c>fetch(request, env, ctx)</c>, which is the shape Cloudflare Workers defines and Deno, Bun,
/// and the framework adapters targeting them all follow. It is named for the handler rather than for
/// any export shape because no standard names the shape, and because a bare function as the default
/// export is accepted here too — what <c>createRequestHandler</c>-style factories produce, and a
/// leniency the convention itself does not have.
///
/// Conformance stops at the handler. The convention's other exports — <c>scheduled</c>,
/// <c>queue</c>, <c>tail</c> — are not called, this being an HTTP handler; and nothing is asked of
/// an application beyond <c>fetch</c>. Asynchronous per-engine startup is the application's own to
/// memoize in module scope, as it would be on Workers, where module scope is per isolate and here it
/// is per engine.
///
/// The module evaluates untouched: no adapter is appended and nothing is serialized on the way in
/// or out. The <c>Request</c> and <c>Response</c> objects are built and taken apart directly on the
/// engine's thread, where those types live.
///
/// A framework whose server protocol does not lower to a fetch handler gets its own handler
/// instead. Reading routes needs no handler of its own: pair an <see cref="INodeRouteProvider"/>
/// with this one.
///
/// Open to derivation, for the specialization that is a variation on this handler rather than a
/// protocol of its own: a host with something to add to the request, or to say about the response,
/// overrides <see cref="SendAsync"/> and calls back. A handler speaking some other protocol
/// implements <see cref="INodeRequestHandler"/> directly instead — that is the seam for a different
/// conversation with the application, where this is the seam for the same one held differently.
/// </remarks>
public class FetchRequestHandler : INodeRequestHandler
{

	/// <summary>
	/// The execution context handed to the handler as its third argument.
	/// </summary>
	/// <remarks>
	/// <c>waitUntil</c> exists on Workers because the isolate is frozen once the response is
	/// returned, and work not registered with it is killed. A pooled engine is not frozen — it keeps
	/// running — so the promise proceeds whether or not anything registers it, and all this has to do
	/// is see that a rejection does not go unobserved. <c>passThroughOnException</c> is honestly a
	/// no-op: there is no origin behind this to pass a request through to.
	/// </remarks>
	const string ContextScript = """
		({
			waitUntil(promise) {
				Promise.resolve(promise).catch(e =>
					console.error('[Alethic.AspNetCore.Node] waitUntil rejected:', e));
			},
			passThroughOnException() { },
		})
		""";

	readonly NodeEnginePool pool;
	readonly NodeModuleSource module;
	readonly Dictionary<string, string> environment;
	readonly ILogger logger;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <remarks>
	/// Holds nothing beyond this. The module is Node's to load and cache; the environment is rebuilt
	/// per request from these values, being input rather than a place to keep things. An application
	/// needing asynchronous startup memoizes it in module scope, which is cached per engine because
	/// that is what a module is — the same thing a Workers application does for per-isolate setup.
	///
	/// The logger is optional so that the ordinary case reads as one <c>new</c>.
	/// </remarks>
	/// <param name="pool"></param>
	/// <param name="options"></param>
	/// <param name="logger"></param>
	public FetchRequestHandler(NodeEnginePool pool, FetchRequestHandlerOptions options, ILogger<FetchRequestHandler>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(options);

		this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
		this.logger = logger ?? NullLogger<FetchRequestHandler>.Instance;

		module = options.Module ?? throw new ArgumentException("A handler needs a module.", nameof(options));
		environment = new Dictionary<string, string>(options.Environment);
	}

	/// <inheritdoc />
	/// <remarks>
	/// Loads the module on every engine, so that evaluating it — which occupies an engine's event
	/// loop — happens at startup rather than under whichever request arrives first. A module that
	/// cannot be loaded fails here, and with it the deployment.
	/// </remarks>
	public virtual Task PrepareAsync(CancellationToken cancellationToken = default) =>
		pool.PrepareAsync(lease => lease.ImportAsync(module, cancellationToken), cancellationToken);

	/// <summary>
	/// Builds the environment object the handler receives. Must be called on the engine's thread.
	/// </summary>
	JSValue BuildEnvironment()
	{
		var env = JSValue.CreateObject();
		foreach (var (key, value) in environment)
			env[key] = value;

		return env;
	}

	/// <inheritdoc />
	public virtual async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		var url = request.RequestUri?.ToString() ?? throw new InvalidOperationException("Request has no URI.");
		var method = request.Method.Method;
		var headers = CollectHeaders(request);

		byte[]? body = null;
		if (request.Content is not null)
			body = await request.Content.ReadAsByteArrayAsync(cancellationToken);

		var lease = await pool.AcquireAsync(cancellationToken);

		try
		{
			var pipe = new Pipe();
			var head = new TaskCompletionSource<ResponseHead>(TaskCreationOptions.RunContinuationsAsynchronously);

			// The render outlives this method: the response is returned once its head is known, and
			// its body continues to arrive afterwards. Faults before the head surface to the caller;
			// faults after it can only truncate the body, the status having already been settled.
			var pump = PumpAsync(lease, url, method, headers, body, pipe.Writer, head, cancellationToken);

			var completed = await Task.WhenAny(head.Task, pump);
			if (completed == pump)
				await pump; // faulted before producing a head; observe the exception

			var headValue = await head.Task;

			var response = new HttpResponseMessage((HttpStatusCode)headValue.Status)
			{
				RequestMessage = request,

				// Disposing the content disposes this stream, and this stream's disposal releases the
				// lease and observes the pump — the lifetime lands where callers already manage it.
				Content = new StreamContent(new ResponseBodyStream(pipe.Reader.AsStream(), lease, pump)),
			};

			foreach (var (name, value) in headValue.Headers)
				if (response.Headers.TryAddWithoutValidation(name, value) == false)
					response.Content.Headers.TryAddWithoutValidation(name, value);

			return response;
		}
		catch
		{
			await lease.DisposeAsync();
			throw;
		}
	}

	/// <summary>
	/// Dispatches the request on the lease's engine and drains the response body into the pipe.
	/// </summary>
	/// <remarks>
	/// One trip onto the engine's thread for the whole render. Everything held across an await goes
	/// through a <see cref="JSReference"/>, awaiting being what ends a value's scope; the abort
	/// controller is additionally posted back rather than called from the cancellation thread,
	/// because that is where it lives.
	/// </remarks>
	/// <param name="lease"></param>
	/// <param name="url"></param>
	/// <param name="method"></param>
	/// <param name="headers"></param>
	/// <param name="body"></param>
	/// <param name="writer"></param>
	/// <param name="head"></param>
	/// <param name="cancellationToken"></param>
	async Task PumpAsync(NodeEngineLease lease, string url, string method, List<KeyValuePair<string, string>> headers, byte[]? body, PipeWriter writer, TaskCompletionSource<ResponseHead> head, CancellationToken cancellationToken)
	{
		try
		{
			await lease.RunAsync(module, async exports =>
			{
				// One leniency the convention does not have: a bare function as the default export is
				// the fetch handler itself, which is what createRequestHandler-style factories produce.
				var app = NodeModuleExports.Default(exports);
				var fetch = app.IsFunction() ? app : app.IsNullOrUndefined() ? app : app["fetch"];
				if (fetch.IsFunction() == false)
					throw new InvalidOperationException($"Module '{module.Name}' has no default export with a fetch function.");

				var controller = JSValue.RunScript("new AbortController()");
				using var controllerRef = new JSReference(controller, isWeak: false);
				using var registration = cancellationToken.Register(() => lease.TryPost(
					() => controllerRef.GetValue().CallMethod("abort", "the request was aborted")));

				var request = BuildRequest(url, method, headers, body, controller["signal"]);
				// Three arguments, as the convention specifies: the request, the host environment, and an
				// execution context. A handler reaching for ctx.waitUntil finds it rather than undefined.
				var pending = fetch.Call(app.IsFunction() ? JSValue.Undefined : app, request, BuildEnvironment(), JSValue.RunScript(ContextScript));

				// Scope ends at the await; everything above but the references is invalid after it.
				var response = await ((JSPromise)JSValue.Global["Promise"].CallMethod("resolve", pending)).AsTask();

				head.TrySetResult(new ResponseHead((int)response["status"], CollectResponseHeaders(response)));

				var stream = response["body"];
				if (stream.IsNullOrUndefined())
					return 0;

				using var reader = new JSReference(stream.CallMethod("getReader"), isWeak: false);

				while (true)
				{
					var chunk = await ((JSPromise)reader.GetValue().CallMethod("read")).AsTask();
					if ((bool)chunk["done"])
						break;

					// Copied into .NET memory while still inside the scope that produced it.
					var copied = ((JSTypedArray<byte>)chunk["value"]).Span.ToArray();
					var flushed = await writer.WriteAsync(copied, CancellationToken.None);
					if (flushed.IsCompleted)
						break; // the consumer gave up on the body
				}

				return 0;
			}, cancellationToken);

			await writer.CompleteAsync();
		}
		catch (Exception e)
		{
			logger.LogDebug(e, "Render of module {Module} failed.", module.Name);
			head.TrySetException(e);
			await writer.CompleteAsync(e);
		}
	}

	/// <summary>
	/// Builds the runtime's own <c>Request</c>, value by value. Must be called on the engine's thread.
	/// </summary>
	/// <param name="url"></param>
	/// <param name="method"></param>
	/// <param name="headers"></param>
	/// <param name="body"></param>
	/// <param name="signal"></param>
	static JSValue BuildRequest(string url, string method, List<KeyValuePair<string, string>> headers, byte[]? body, JSValue signal)
	{
		var init = JSValue.CreateObject();
		init["method"] = method;
		init["signal"] = signal;

		// Headers in fetch's pair form: an array of [name, value] arrays.
		var pairs = JSValue.CreateArray(headers.Count);
		for (var i = 0; i < headers.Count; i++)
		{
			var pair = JSValue.CreateArray(2);
			pair[0] = headers[i].Key;
			pair[1] = headers[i].Value;
			pairs[i] = pair;
		}

		init["headers"] = pairs;

		if (body is not null)
			init["body"] = new JSTypedArray<byte>(body);

		return JSValue.Global["Request"].CallAsConstructor(url, init);
	}

	/// <summary>
	/// Reads the response's headers out through their own iterator. Must be called on the engine's
	/// thread.
	/// </summary>
	/// <param name="response"></param>
	static List<KeyValuePair<string, string>> CollectResponseHeaders(JSValue response)
	{
		var headers = new List<KeyValuePair<string, string>>();
		var entries = response["headers"].CallMethod("entries");

		while (true)
		{
			var step = entries.CallMethod("next");
			if ((bool)step["done"])
				break;

			var pair = step["value"];
			headers.Add(new((string)pair[0], (string)pair[1]));
		}

		return headers;
	}

	/// <summary>
	/// Flattens the request's headers, content headers included.
	/// </summary>
	/// <param name="request"></param>
	static List<KeyValuePair<string, string>> CollectHeaders(HttpRequestMessage request)
	{
		var headers = new List<KeyValuePair<string, string>>();

		foreach (var header in request.Headers)
			foreach (var value in header.Value)
				headers.Add(new(header.Key, value));

		if (request.Content is not null)
			foreach (var header in request.Content.Headers)
				foreach (var value in header.Value)
					headers.Add(new(header.Key, value));

		return headers;
	}

	/// <summary>
	/// The part of a response known before its body has arrived.
	/// </summary>
	/// <param name="Status"></param>
	/// <param name="Headers"></param>
	readonly record struct ResponseHead(int Status, List<KeyValuePair<string, string>> Headers);

}
