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
	/// The module this application dispatches to, before decoration.
	/// </summary>
	JavaScriptModuleSource Source { get; }

	/// <summary>
	/// Dispatches a request to the application's default <c>fetch</c> export.
	/// </summary>
	/// <remarks>
	/// The response streams: it is returned as soon as the application answers, and its content
	/// continues to fill afterwards, so a caller wanting the whole body must read it to the end. A
	/// failure after the first byte can truncate the body but not change the status. Cancellation
	/// aborts the work itself, through the request's <c>AbortSignal</c>, and the engine capacity the
	/// render holds is returned when the response content is disposed.
	/// </remarks>
	/// <param name="request"></param>
	/// <param name="cancellationToken"></param>
	Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);

	/// <summary>
	/// Asks the application for its route manifest, as JSON text, or null where it offers none.
	/// </summary>
	/// <param name="export">Name of the application's manifest export, <c>routes</c> conventionally.</param>
	/// <param name="cancellationToken"></param>
	Task<string?> GetRoutesJsonAsync(string export, CancellationToken cancellationToken);

}
