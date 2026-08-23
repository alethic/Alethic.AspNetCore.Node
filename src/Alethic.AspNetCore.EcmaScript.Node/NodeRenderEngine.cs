using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.JavaScript.NodeApi;

namespace Alethic.AspNetCore.EcmaScript.Node;

/// <summary>
/// A rendering engine backed by a Node engine pool.
/// </summary>
/// <remarks>
/// Implements the HTTP request/response abstraction over the application's <c>fetch</c> export. All
/// the machinery — the adapter glue, how requests travel, the module, the pool — is private to this
/// class; nothing above sees anything but HTTP.
/// </remarks>
public sealed class NodeRenderEngine : IRenderEngine
{

	/// <summary>
	/// The adapter appended to the application's module.
	/// </summary>
	/// <remarks>
	/// The <c>Request</c> and <c>Response</c> types are the runtime's, so the place to construct and
	/// take them apart is inside the runtime rather than from the host. That the request and the
	/// response head travel as JSON text is likewise a private choice made here, invisible on either
	/// side of this class. The glue evaluates in the module's own CommonJS scope and adds exports
	/// alongside the application's.
	/// </remarks>
	const string Glue = """

		// --- appended by Alethic.AspNetCore.EcmaScript.Node ---
		module.exports.__alethicHandle = function (requestJson, payload, signal) {
			const app = module.exports.default;
			const req = JSON.parse(requestJson);
			const request = new Request(req.url, {
				method: req.method,
				headers: req.headers,
				body: payload ?? undefined,
				signal: signal,
			});
			return Promise.resolve(app.fetch(request)).then(response => ({
				head: JSON.stringify({
					status: response.status,
					headers: Array.from(response.headers.entries()),
				}),
				body: response.body,
			}));
		};
		module.exports.__alethicRoutes = function (name) {
			const app = module.exports.default;
			const fn = app ? app[name] : undefined;
			if (typeof fn !== 'function')
				return null;
			return Promise.resolve(fn.call(app)).then(routes => routes == null ? null : JSON.stringify(routes));
		};
		""";

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

		var inner = options.Module ?? throw new ArgumentException("A rendering engine needs a module.", nameof(options));
		module = new GluedSource(inner);
	}

	/// <inheritdoc />
	public Task PrepareAsync(CancellationToken cancellationToken = default) =>
		pool.PrepareAsync(lease => lease.ImportAsync(module, cancellationToken), cancellationToken);

	/// <inheritdoc />
	public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		var requestJson = JsonSerializer.Serialize(new RequestDescriptor()
		{
			Url = request.RequestUri?.ToString() ?? throw new InvalidOperationException("Request has no URI."),
			Method = request.Method.Method,
			Headers = CollectHeaders(request),
		});

		byte[]? body = null;
		if (request.Content is not null)
			body = await request.Content.ReadAsByteArrayAsync(cancellationToken);

		var lease = await pool.AcquireAsync(cancellationToken);

		try
		{
			var pipe = new Pipe();
			var head = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

			// The render outlives this method: the response is returned once its head is known, and
			// its body continues to arrive afterwards. Faults before the head surface to the caller;
			// faults after it can only truncate the body, the status having already been settled.
			var pump = PumpAsync(lease, requestJson, body, pipe.Writer, head, cancellationToken);

			var completed = await Task.WhenAny(head.Task, pump);
			if (completed == pump)
				await pump; // faulted before producing a head; observe the exception

			var headValue = JsonSerializer.Deserialize<ResponseHead>(await head.Task
				?? throw new InvalidOperationException("The application's fetch produced no response."))
				?? throw new InvalidOperationException("The application's fetch produced no response.");

			var response = new HttpResponseMessage((HttpStatusCode)headValue.Status)
			{
				RequestMessage = request,

				// Disposing the content disposes this stream, and this stream's disposal releases the
				// lease and observes the pump — the lifetime lands where callers already manage it.
				Content = new StreamContent(new RenderBodyStream(pipe.Reader.AsStream(), lease, pump)),
			};

			foreach (var pair in headValue.Headers)
				if (response.Headers.TryAddWithoutValidation(pair[0], pair[1]) == false)
					response.Content.Headers.TryAddWithoutValidation(pair[0], pair[1]);

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

		var json = await lease.RunAsync(module, async exports =>
		{
			var pending = exports.CallMethod("__alethicRoutes", options.RoutesExport);
			var settled = await ((JSPromise)JSValue.Global["Promise"].CallMethod("resolve", pending)).AsTask();
			return settled.IsNullOrUndefined() ? null : (string?)settled;
		}, cancellationToken);

		return json is null ? null : JsonSerializer.Deserialize<List<RenderRoute>>(json);
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
	/// <param name="requestJson"></param>
	/// <param name="body"></param>
	/// <param name="writer"></param>
	/// <param name="head"></param>
	/// <param name="cancellationToken"></param>
	async Task PumpAsync(NodeEngineLease lease, string requestJson, byte[]? body, PipeWriter writer, TaskCompletionSource<string?> head, CancellationToken cancellationToken)
	{
		try
		{
			await lease.RunAsync(module, async exports =>
			{
				var controller = JSValue.RunScript("new AbortController()");
				using var controllerRef = new JSReference(controller, isWeak: false);
				using var registration = cancellationToken.Register(() => lease.TryPost(
					() => controllerRef.GetValue().CallMethod("abort", "the request was aborted")));

				var payload = body is null ? JSValue.Null : new JSTypedArray<byte>(body);
				var pending = exports.CallMethod("__alethicHandle", requestJson, payload, controller["signal"]);

				// Scope ends at the await; everything above but the references is invalid after it.
				var result = await ((JSPromise)JSValue.Global["Promise"].CallMethod("resolve", pending)).AsTask();

				head.TrySetResult((string?)result["head"]);

				var stream = result["body"];
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
	/// Flattens the request's headers, content headers included, into fetch's pair form.
	/// </summary>
	/// <param name="request"></param>
	static List<string[]> CollectHeaders(HttpRequestMessage request)
	{
		var headers = new List<string[]>();

		foreach (var header in request.Headers)
			foreach (var value in header.Value)
				headers.Add([header.Key, value]);

		if (request.Content is not null)
			foreach (var header in request.Content.Headers)
				foreach (var value in header.Value)
					headers.Add([header.Key, value]);

		return headers;
	}

	/// <summary>
	/// The application's module with the adapter appended.
	/// </summary>
	sealed class GluedSource : NodeModuleSource
	{

		readonly NodeModuleSource inner;

		/// <summary>
		/// Initializes a new instance.
		/// </summary>
		/// <param name="inner"></param>
		public GluedSource(NodeModuleSource inner)
		{
			this.inner = inner;
		}

		/// <inheritdoc />
		public override string Key => "render:" + inner.Key;

		/// <inheritdoc />
		public override string Name => inner.Name;

		/// <inheritdoc />
		public override async ValueTask<string> ReadAsync(CancellationToken cancellationToken) =>
			await inner.ReadAsync(cancellationToken) + Glue;

	}

	/// <summary>
	/// The request as the glue receives it.
	/// </summary>
	sealed class RequestDescriptor
	{

		/// <summary>
		/// Absolute request URL.
		/// </summary>
		[JsonPropertyName("url")]
		public required string Url { get; init; }

		/// <summary>
		/// HTTP method.
		/// </summary>
		[JsonPropertyName("method")]
		public required string Method { get; init; }

		/// <summary>
		/// Headers in fetch's pair form.
		/// </summary>
		[JsonPropertyName("headers")]
		public required List<string[]> Headers { get; init; }

	}

	/// <summary>
	/// The response head as the glue reports it.
	/// </summary>
	sealed class ResponseHead
	{

		/// <summary>
		/// HTTP status code.
		/// </summary>
		[JsonPropertyName("status")]
		public int Status { get; init; }

		/// <summary>
		/// Headers in fetch's pair form.
		/// </summary>
		[JsonPropertyName("headers")]
		public List<string[]> Headers { get; init; } = [];

	}

}
