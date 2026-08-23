using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// Speaks the fetch contract to a pooled module through plain object operations.
/// </summary>
sealed class JavaScriptHttpApplication : IJavaScriptHttpApplication
{

	readonly IJavaScriptEnginePool pool;
	readonly JavaScriptModuleSource source;
	readonly HttpModuleSource decorated;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="pool"></param>
	/// <param name="source"></param>
	public JavaScriptHttpApplication(IJavaScriptEnginePool pool, JavaScriptModuleSource source)
	{
		this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
		this.source = source ?? throw new ArgumentNullException(nameof(source));
		decorated = new HttpModuleSource(source);
	}

	/// <inheritdoc />
	public JavaScriptModuleSource Source => source;

	/// <inheritdoc />
	public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

		// Everything from here shares the session's engine, and everything acquired along the way is
		// owned by the body stream, which is the last thing standing when the response is done.
		var session = await pool.AcquireAsync(decorated, cancellationToken);
		var owned = new List<IJavaScriptObject>();
		var registration = default(CancellationTokenRegistration);

		try
		{
			// The abort controller is how cancellation reaches the render itself: the registration
			// fires on some .NET thread and the abort is an ordinary method call on the handle. The
			// callback swallows everything: it may race the handle's disposal, and a callback that
			// throws does so inside the token's own cancellation processing, which belongs to
			// whoever cancelled — the server's teardown, typically — and must not be broken.
			var controller = (await session.Engine.EvaluateAsync("new AbortController()", cancellationToken)).AsObject();
			owned.Add(controller);
			registration = cancellationToken.Register(() =>
			{
				try
				{
					_ = controller.InvokeAsync("abort", ["the request was aborted"], CancellationToken.None);
				}
				catch
				{
				}
			});

			var signal = await controller.GetAsync("signal", cancellationToken);
			if (signal.Kind == JavaScriptValueKind.Object)
				owned.Add(signal.AsObject());

			var payload = JavaScriptValue.Null;
			if (body is not null)
			{
				payload = await session.Engine.CreateByteArrayAsync(body, cancellationToken);
				owned.Add(payload.AsObject());
			}

			var pending = (await session.Module.InvokeAsync(HttpModuleSource.HandleExport, [requestJson, payload, signal], cancellationToken)).AsObject();
			owned.Add(pending);

			var result = (await pending.AwaitAsync(cancellationToken)).AsObject();
			owned.Add(result);

			var head = JsonSerializer.Deserialize<ResponseHead>((await result.GetAsync("head", cancellationToken)).AsString())
				?? throw new InvalidOperationException("The application's fetch produced no response head.");

			var bodyValue = await result.GetAsync("body", cancellationToken);
			var stream = bodyValue.Kind == JavaScriptValueKind.Object
				? await JavaScriptResponseBodyStream.OpenAsync(session, bodyValue.AsObject(), registration, owned, cancellationToken)
				: JavaScriptResponseBodyStream.Empty(session, registration, owned);

			var response = new HttpResponseMessage((HttpStatusCode)head.Status)
			{
				RequestMessage = request,

				// Disposing the content disposes the stream, and the stream owns the session and
				// every handle taken along the way — the ordinary dispose-the-response flow is enough
				// to return everything.
				Content = new StreamContent(stream),
			};

			foreach (var pair in head.Headers)
				if (response.Headers.TryAddWithoutValidation(pair[0], pair[1]) == false)
					response.Content.Headers.TryAddWithoutValidation(pair[0], pair[1]);

			return response;
		}
		catch
		{
			// The registration goes first: once the handles are gone, a late cancellation would find
			// nothing valid to call.
			await registration.DisposeAsync();

			foreach (var handle in owned)
				await handle.DisposeAsync();

			await session.DisposeAsync();
			throw;
		}
	}

	/// <inheritdoc />
	public async Task<string?> GetRoutesJsonAsync(string export, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(export);

		await using var session = await pool.AcquireAsync(decorated, cancellationToken);

		var pending = await session.Module.InvokeAsync(HttpModuleSource.RoutesExport, [export], cancellationToken);
		if (pending.Kind != JavaScriptValueKind.Object)
			return null;

		await using var handle = pending.AsObject();
		var settled = await handle.AwaitAsync(cancellationToken);
		return settled.IsNullish ? null : settled.AsString();
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
