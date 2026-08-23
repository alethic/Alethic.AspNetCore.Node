using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// Speaks the fetch contract to a pooled module through the general streaming invocation.
/// </summary>
sealed class JavaScriptHttpApplication : IJavaScriptHttpApplication
{

	readonly IJavaScriptModule module;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="module">The pooled module, evaluated from an <see cref="HttpModuleSource"/>.</param>
	public JavaScriptHttpApplication(IJavaScriptModule module)
	{
		this.module = module ?? throw new ArgumentNullException(nameof(module));
	}

	/// <inheritdoc />
	public IJavaScriptModule Module => module;

	/// <inheritdoc />
	public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var descriptor = new RequestDescriptor()
		{
			Url = request.RequestUri?.ToString() ?? throw new InvalidOperationException("Request has no URI."),
			Method = request.Method.Method,
			Headers = CollectHeaders(request),
		};

		ReadOnlyMemory<byte>? payload = null;
		if (request.Content is not null)
			payload = await request.Content.ReadAsByteArrayAsync(cancellationToken);

		var result = await module.InvokeStreamAsync(HttpModuleSource.HandleExport, [descriptor], payload, cancellationToken);

		try
		{
			var head = result.GetHead<ResponseHead>()
				?? throw new InvalidOperationException("The application's fetch produced no response head.");

			var response = new HttpResponseMessage((HttpStatusCode)head.Status)
			{
				RequestMessage = request,

				// Disposing the content disposes this stream, and this stream's disposal disposes
				// the whole stream response — which is what releases the engine capacity the render
				// is still holding. The lifetime lands where callers already manage it.
				Content = new StreamContent(new StreamResponseStream(result)),
			};

			foreach (var pair in head.Headers)
				if (response.Headers.TryAddWithoutValidation(pair[0], pair[1]) == false)
					response.Content.Headers.TryAddWithoutValidation(pair[0], pair[1]);

			return response;
		}
		catch
		{
			await result.DisposeAsync();
			throw;
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
