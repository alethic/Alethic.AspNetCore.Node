using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

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
    /// Answers one request, writing the response as it is produced.
    /// </summary>
    /// <remarks>
    /// The context rather than a request message, because a request message is already an answer to
    /// how the application should be addressed, and that is the implementation's to decide — an
    /// application whose protocol is not HTTP-shaped should not be handed a shape it did not ask
    /// for. What is here to be read is whatever ASP.NET resolved, the path base among it, which no
    /// request message can carry.
    ///
    /// The response streams: the status and headers are written as soon as they are known and the
    /// body follows as it arrives, so a page emitting its shell ahead of suspended content reaches
    /// the client that way. A failure after the first byte can truncate the body but cannot change
    /// the status. Cancellation, through <see cref="HttpContext.RequestAborted"/>, aborts the work
    /// itself and not merely the wait for it.
    ///
    /// Nothing outlives the call. Whatever engine capacity the implementation claimed is released
    /// before it returns, which is what makes the claim an ordinary scope rather than something the
    /// caller has to remember to dispose.
    /// </remarks>
    /// <param name="context"></param>
    Task HandleAsync(HttpContext context);

}
