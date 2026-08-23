using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
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
	readonly JSReference module;
	readonly ILogger logger;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="engine"></param>
	/// <param name="source"></param>
	/// <param name="module"></param>
	/// <param name="logger"></param>
	public NodeModuleInstance(NodeEngine engine, JavaScriptModuleSource source, JSReference module, ILogger logger)
	{
		this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
		this.source = source ?? throw new ArgumentNullException(nameof(source));
		this.module = module ?? throw new ArgumentNullException(nameof(module));
		this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc />
	public JavaScriptModuleSource Source => source;

	/// <inheritdoc />
	public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var pipe = new Pipe();
		var head = new TaskCompletionSource<ResponseHead>(TaskCreationOptions.RunContinuationsAsynchronously);
		byte[]? body = null;

		if (request.Content is not null)
			body = await request.Content.ReadAsByteArrayAsync(cancellationToken);

		// The render outlives this method: the response is returned once its head is known, and its
		// body continues to arrive afterwards. Faults before the head surface to the caller; faults
		// after it can only truncate the body, the status line having already been settled.
		var pump = PumpAsync(request, body, pipe.Writer, head, cancellationToken);

		var completed = await Task.WhenAny(head.Task, pump);
		if (completed == pump)
			await pump; // faulted before producing a head; observe the exception

		var value = await head.Task;
		var response = new HttpResponseMessage((HttpStatusCode)value.Status)
		{
			RequestMessage = request,
			Content = new StreamContent(pipe.Reader.AsStream()),
		};

		foreach (var (name, header) in value.Headers)
			if (response.Headers.TryAddWithoutValidation(name, header) == false)
				response.Content.Headers.TryAddWithoutValidation(name, header);

		return response;
	}

	/// <summary>
	/// Dispatches the request and drains the response body into the pipe.
	/// </summary>
	/// <param name="request"></param>
	/// <param name="body"></param>
	/// <param name="writer"></param>
	/// <param name="head"></param>
	/// <param name="cancellationToken"></param>
	async Task PumpAsync(HttpRequestMessage request, byte[]? body, PipeWriter writer, TaskCompletionSource<ResponseHead> head, CancellationToken cancellationToken)
	{
		var url = request.RequestUri?.ToString() ?? throw new InvalidOperationException("Request has no URI.");
		var method = request.Method.Method;
		var headers = CollectRequestHeaders(request);

		try
		{
			await engine.Runtime.RunAsync(async () =>
			{
				// The controller belongs to the runtime, but cancellation arrives on some other
				// thread, so it is held by reference and the abort is posted back here.
				var controller = JSValue.RunScript("new AbortController()");
				using var controllerRef = new JSReference(controller, isWeak: false);
				using var registration = cancellationToken.Register(static state =>
				{
					var (rt, reference) = ((NodeEngine, JSReference))state!;
					rt.Runtime.Post(() => reference.GetValue().CallMethod("abort", "the request was aborted"), allowSync: false);
				}, (engine, controllerRef));

				var message = BuildRequest(url, method, headers, body, controller["signal"]);

				// fetch may answer synchronously or with a promise; normalizing through the runtime's own
				// Promise.resolve accepts both without caring which.
				var promise = (JSPromise)JSValue.Global["Promise"].CallMethod("resolve", module.GetValue().CallMethod("fetch", message));

				// Scope ends here. Everything above is invalid from the next line on.
				var response = await promise.AsTask();

				head.TrySetResult(new ResponseHead((int)response["status"], CollectResponseHeaders(response)));

				var stream = response["body"];
				if (stream.IsNull() || stream.IsUndefined())
					return 0;

				using var reader = new JSReference(stream.CallMethod("getReader"), isWeak: false);

				while (true)
				{
					var read = (JSPromise)reader.GetValue().CallMethod("read");
					var chunk = await read.AsTask();
					if ((bool)chunk["done"])
						break;

					// Copied into .NET memory while still inside the scope that produced it.
					var bytes = ((JSTypedArray<byte>)chunk["value"]).Span.ToArray();
					var result = await writer.WriteAsync(bytes, CancellationToken.None);
					if (result.IsCompleted)
						break; // the reader gave up on us
				}

				return 0;
			});

			await writer.CompleteAsync();
		}
		catch (Exception e)
		{
			logger.LogDebug(e, "JavaScript module {Module} failed while rendering {Url}.", source.Name, url);
			head.TrySetException(e);
			await writer.CompleteAsync(e);
		}
	}

	/// <summary>
	/// Builds the runtime request. Must be called on the runtime's thread.
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

		var list = JSValue.CreateArray();
		for (var i = 0; i < headers.Count; i++)
		{
			var pair = JSValue.CreateArray();
			pair[0] = headers[i].Key;
			pair[1] = headers[i].Value;
			list[i] = pair;
		}

		init["headers"] = list;

		if (body is not null)
			init["body"] = new JSTypedArray<byte>(body);

		return JSValue.Global["Request"].CallAsConstructor(url, init);
	}

	/// <summary>
	/// Flattens the request's headers, content headers included.
	/// </summary>
	/// <param name="request"></param>
	static List<KeyValuePair<string, string>> CollectRequestHeaders(HttpRequestMessage request)
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
	/// Reads the response headers. Must be called on the runtime's thread.
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

	/// <inheritdoc />
	public async Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(export);
		ArgumentNullException.ThrowIfNull(arguments);

		var json = JsonSerializer.Serialize(arguments);

		var result = await engine.Runtime.RunAsync(async () =>
		{
			var args = JSValue.Global["JSON"].CallMethod("parse", json);
			var target = module.GetValue();
			var value = target.CallMethod(export, ToArray(args));

			// An export may be synchronous or not; awaiting only a promise keeps both usable.
			if (value.IsPromise())
				value = await ((JSPromise)value).AsTask();

			return value.IsUndefined() || value.IsNull()
				? null
				: (string?)JSValue.Global["JSON"].CallMethod("stringify", value);
		});

		return result is null ? default : JsonSerializer.Deserialize<T>(result);
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

	/// <summary>
	/// The part of a response known before its body has arrived.
	/// </summary>
	/// <param name="Status"></param>
	/// <param name="Headers"></param>
	readonly record struct ResponseHead(int Status, List<KeyValuePair<string, string>> Headers);

}
