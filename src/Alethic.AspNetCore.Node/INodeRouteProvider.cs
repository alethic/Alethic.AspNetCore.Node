using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Tells the host what routes an application serves.
/// </summary>
/// <remarks>
/// A separate object from the <see cref="INodeRequestHandler"/> it accompanies, because answering
/// requests and knowing the routes vary independently. The <c>fetch</c> convention describes a
/// handler and says nothing whatever about routes, so an application can want its framework's routes
/// read while needing no request handler of its own — the stock one already answers it. Pairing a
/// provider with a handler costs one object; folding both into one class would cost a wrapper whose
/// only content is forwarding.
///
/// Writing one means knowing a particular framework well enough to read the router the application
/// is actually dispatching on. That is the point: routes come from the one place that already has
/// them, rather than a second declaration maintained alongside and free to drift.
///
/// Mounting without one is not a degradation — it is the honest outcome for a protocol that
/// describes no routes, and the application is served whole from the fallback endpoint.
/// </remarks>
public interface INodeRouteProvider
{

	/// <summary>
	/// The routes the application serves.
	/// </summary>
	/// <remarks>
	/// Answered after <see cref="INodeRequestHandler.PrepareAsync"/>, so the application is
	/// initialized and its router built by the time it is asked.
	///
	/// An application with genuinely no routes answers an empty list. Failure to read the router is
	/// an exception and fails the deployment — a broken extraction is a bug, not an absence, and the
	/// two must not look alike.
	/// </remarks>
	/// <param name="cancellationToken"></param>
	Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken cancellationToken = default);

}
