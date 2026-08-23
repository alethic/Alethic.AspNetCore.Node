using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Wraps content so that disposing it also releases something the content depended on.
/// </summary>
/// <remarks>
/// A streaming response is still being produced when it is handed back, so whatever it is being
/// produced against has to outlive the call that returned it. Tying the release to disposal of the
/// content puts that lifetime where the caller already manages it.
/// </remarks>
sealed class ReleasingHttpContent : HttpContent
{

	readonly HttpContent inner;
	readonly IAsyncDisposable release;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="inner"></param>
	/// <param name="release"></param>
	public ReleasingHttpContent(HttpContent inner, IAsyncDisposable release)
	{
		this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
		this.release = release ?? throw new ArgumentNullException(nameof(release));

		foreach (var header in inner.Headers)
			Headers.TryAddWithoutValidation(header.Key, header.Value);
	}

	/// <inheritdoc />
	protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
		inner.CopyToAsync(stream, context);

	/// <inheritdoc />
	protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
		inner.CopyToAsync(stream, context, cancellationToken);

	/// <inheritdoc />
	protected override Task<Stream> CreateContentReadStreamAsync() =>
		inner.ReadAsStreamAsync();

	/// <inheritdoc />
	protected override bool TryComputeLength(out long length)
	{
		// A response still being rendered has no length to compute.
		length = 0;
		return false;
	}

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			inner.Dispose();

			// Disposal is synchronous here and the release is not expected to block, so observing the
			// task is enough to avoid swallowing a fault.
			release.DisposeAsync().AsTask().GetAwaiter().GetResult();
		}

		base.Dispose(disposing);
	}

}
