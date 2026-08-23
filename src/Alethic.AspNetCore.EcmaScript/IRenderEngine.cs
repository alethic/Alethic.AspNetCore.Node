using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// A rendering engine: something that answers HTTP requests with rendered documents.
/// </summary>
/// <remarks>
/// This is the whole abstraction. How rendering happens — what runtime, what language, whether
/// anything is pooled — is entirely the implementation's affair; nothing of it appears here, and
/// nothing here obliges an implementation to any of it.
/// </remarks>
public interface IRenderEngine
{

	/// <summary>
	/// Brings the engine to readiness ahead of traffic.
	/// </summary>
	/// <remarks>
	/// Called before endpoints are mapped, so whatever startup cost the implementation carries lands
	/// here rather than under the first request — and so a broken engine fails the deployment rather
	/// than quietly serving nothing.
	/// </remarks>
	/// <param name="cancellationToken"></param>
	Task PrepareAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Renders one request.
	/// </summary>
	/// <remarks>
	/// The response streams: it is returned as soon as its status and headers are known, and its
	/// content continues to arrive afterwards, so a caller wanting the whole body reads it to the
	/// end. A failure after the first byte can truncate the body but not change the status.
	/// Cancellation aborts the rendering itself, not merely the wait for it, and whatever the render
	/// holds is released when the response is disposed.
	/// </remarks>
	/// <param name="request"></param>
	/// <param name="cancellationToken"></param>
	Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);

	/// <summary>
	/// The application's route manifest, or null where it offers none.
	/// </summary>
	/// <remarks>
	/// The manifest comes from the application's own router, translated to ASP.NET template syntax by
	/// the application itself — the one place that knows its framework — so nothing is declared
	/// twice and nothing can drift.
	/// </remarks>
	/// <param name="cancellationToken"></param>
	Task<IReadOnlyList<RenderRoute>?> GetRoutesAsync(CancellationToken cancellationToken = default);

}
