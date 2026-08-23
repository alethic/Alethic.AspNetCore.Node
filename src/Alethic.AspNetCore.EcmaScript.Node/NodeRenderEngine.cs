using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.JavaScript.NodeApi;

namespace Alethic.AspNetCore.EcmaScript.Node;

/// <summary>
/// A rendering engine backed by a Node engine pool.
/// </summary>
/// <remarks>
/// Implements the HTTP request/response abstraction over the application's <c>fetch</c> export. The
/// module evaluates untouched — no adapter is appended and nothing is serialized on the way in or
/// out. The <c>Request</c> and <c>Response</c> objects are built and taken apart directly on the
/// engine's thread, where those types live.
/// </remarks>
public sealed class NodeRenderEngine : IRenderEngine
{

	readonly NodeEnginePool pool;
	readonly NodeRenderEngineOptions options;
	readonly ILogger logger;
	readonly NodeModuleSource module;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="pool"></param>
	/// <param name="options"></param>
	/// <param name="logger"></param>
	public NodeRenderEngine(NodeEnginePool pool, NodeRenderEngineOptions options, ILogger<NodeRenderEngine> logger)
	{
		this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
		this.options = options ?? throw new ArgumentNullException(nameof(options));
		this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

		module = options.Module ?? throw new ArgumentException("A rendering engine needs a module.", nameof(options));
	}

	/// <inheritdoc />
	public Task PrepareAsync(CancellationToken cancellationToken = default) =>
		pool.PrepareAsync(lease => lease.ImportAsync(module, cancellationToken), cancellationToken);

	/// <inheritdoc />
	public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
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
				Content = new StreamContent(new RenderBodyStream(pipe.Reader.AsStream(), lease, pump)),
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

	/// <inheritdoc />
	public async Task<IReadOnlyList<RenderRoute>?> GetRoutesAsync(CancellationToken cancellationToken = default)
	{
		await using var lease = await pool.AcquireAsync(cancellationToken);

		return await lease.RunAsync(module, async exports =>
		{
			var app = exports["default"];
			if (app.IsNullOrUndefined())
				return null;

			var function = app[options.RoutesExport];
			if (function.IsFunction() == false)
				return null;

			var settled = await ((JSPromise)JSValue.Global["Promise"].CallMethod("resolve", function.Call(app))).AsTask();
			if (settled.IsNullOrUndefined())
				return null;

			// Read the entries directly off the array; nothing is serialized on the way over.
			var routes = new List<RenderRoute>();
			var length = (int)settled["length"];

			for (var i = 0; i < length; i++)
			{
				var entry = settled[i];

				var pattern = entry["pattern"];
				var id = entry["id"];
				var mode = entry["renderMode"];

				routes.Add(new RenderRoute()
				{
					Pattern = pattern.IsNullOrUndefined() ? null : (string)pattern,
					Id = id.IsNullOrUndefined() ? null : (string)id,
					RenderMode = mode.IsNullOrUndefined() == false && Enum.TryParse<RenderMode>((string)mode, ignoreCase: true, out var parsed)
						? parsed
						: RenderMode.Server,
				});
			}

			return (IReadOnlyList<RenderRoute>?)routes;
		}, cancellationToken);
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
				var app = exports["default"];
				if (app.IsNullOrUndefined() || app["fetch"].IsFunction() == false)
					throw new InvalidOperationException($"Module '{module.Name}' has no default export with a fetch function.");

				var controller = JSValue.RunScript("new AbortController()");
				using var controllerRef = new JSReference(controller, isWeak: false);
				using var registration = cancellationToken.Register(() => lease.TryPost(
					() => controllerRef.GetValue().CallMethod("abort", "the request was aborted")));

				var request = BuildRequest(url, method, headers, body, controller["signal"]);
				var pending = app["fetch"].Call(app, request);

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
