using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Answers HTTP requests from an application on the embedded Node runtime.
/// </summary>
/// <remarks>
/// A request handler, not an engine: an engine here is a libnode runtime on its own thread, of which
/// a <see cref="NodeEnginePool"/> holds several. A request handler is what uses them to answer
/// requests.
///
/// The runtime is not in question — it is libnode, and every implementation runs on those same
/// engines. What varies is the convention the application on the other side speaks: a Web-standard
/// <c>fetch</c> handler, a framework's own server protocol where it has one that does not lower to
/// that, and whatever else earns an implementation. This is the seam those meet at, so the endpoint
/// layer is written once and none of it has to know which application it is talking to.
///
/// Answering requests is all this covers. Reading an application's routes is a separate object,
/// <see cref="INodeRouteProvider"/>, passed alongside a request handler rather than folded into one,
/// because the two vary independently.
/// </remarks>
public interface INodeRequestHandler
{

	/// <summary>
	/// Brings the handler to readiness ahead of traffic.
	/// </summary>
	/// <remarks>
	/// Called before endpoints are mapped, so whatever startup cost the implementation carries lands
	/// here rather than under the first request — and so a broken application fails the deployment
	/// rather than quietly serving nothing.
	/// </remarks>
	/// <param name="cancellationToken"></param>
	Task PrepareAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Answers one request.
	/// </summary>
	/// <remarks>
	/// The response streams: it is returned as soon as its status and headers are known, and its
	/// content continues to arrive afterwards, so a caller wanting the whole body reads it to the
	/// end. A failure after the first byte can truncate the body but not change the status.
	/// Cancellation aborts the work itself, not merely the wait for it, and whatever that work holds
	/// is released when the response is disposed.
	/// </remarks>
	/// <param name="request"></param>
	/// <param name="cancellationToken"></param>
	Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);

}
