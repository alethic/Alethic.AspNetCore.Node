using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// The result of a streaming invocation: a structured head, known up front, and a body that fills as
/// the module produces it.
/// </summary>
/// <remarks>
/// Dispose releases whatever the invocation held — the body's producer, and any capacity charged for
/// the call — so a response must be disposed whether or not its body was read.
/// </remarks>
public sealed class JavaScriptStreamResponse : IAsyncDisposable
{

	readonly string? headJson;
	readonly Stream body;
	readonly IAsyncDisposable? release;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="headJson">The head as JSON text, or null when the export produced none.</param>
	/// <param name="body"></param>
	/// <param name="release"></param>
	public JavaScriptStreamResponse(string? headJson, Stream body, IAsyncDisposable? release = null)
	{
		this.headJson = headJson;
		this.body = body ?? throw new ArgumentNullException(nameof(body));
		this.release = release;
	}

	/// <summary>
	/// The head as JSON text, or null when the export produced none.
	/// </summary>
	public string? HeadJson => headJson;

	/// <summary>
	/// Converts the head from JSON.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public T? GetHead<T>() => headJson is null ? default : JsonSerializer.Deserialize<T>(headJson);

	/// <summary>
	/// The body, delivered as the module produces it. Empty when the export declared none.
	/// </summary>
	public Stream Body => body;

	/// <summary>
	/// Returns a response equal to this one that also releases the given resource on disposal.
	/// </summary>
	/// <param name="resource"></param>
	public JavaScriptStreamResponse WithRelease(IAsyncDisposable resource) =>
		new(headJson, body, release is null ? resource : new Both(release, resource));

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		await body.DisposeAsync();

		if (release is not null)
			await release.DisposeAsync();
	}

	/// <summary>
	/// Two resources released as one.
	/// </summary>
	/// <param name="first"></param>
	/// <param name="second"></param>
	sealed class Both(IAsyncDisposable first, IAsyncDisposable second) : IAsyncDisposable
	{

		/// <inheritdoc />
		public async ValueTask DisposeAsync()
		{
			await first.DisposeAsync();
			await second.DisposeAsync();
		}

	}

}
