using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// A JavaScript application spoken to through the Web-standard fetch contract.
/// </summary>
public interface IJavaScriptHttpApplication
{

	/// <summary>
	/// The underlying module, for exports beyond fetch — a route manifest, sitemap data, or whatever
	/// else the application publishes.
	/// </summary>
	IJavaScriptModule Module { get; }

	/// <summary>
	/// Dispatches a request to the application's default <c>fetch</c> export.
	/// </summary>
	/// <remarks>
	/// The response streams: it is returned as soon as the application answers, and its content
	/// continues to fill afterwards, so a caller wanting the whole body must read it to the end. A
	/// failure after the first byte can truncate the body but not change the status. Cancellation
	/// aborts the work itself, through the request's <c>AbortSignal</c>.
	/// </remarks>
	/// <param name="request"></param>
	/// <param name="cancellationToken"></param>
	Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);

}
