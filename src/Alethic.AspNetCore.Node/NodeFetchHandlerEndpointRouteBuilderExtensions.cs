using System;

using Alethic.AspNetCore.Node;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Mounts an application through its <c>fetch</c> handler.
/// </summary>
/// <remarks>
/// The convenience for one implementation, kept apart from
/// <see cref="NodeEndpointRouteBuilderExtensions"/> so that the general mounting API stays
/// general. <see cref="FetchRequestHandler"/> is one <see cref="INodeRequestHandler"/> among however many
/// a framework's own server protocol warrants, and the API should not read as though it were the
/// only one — a handler written for some other protocol brings its own <c>Map</c> alongside this,
/// exactly as ASP.NET's own features do.
/// </remarks>
public static class NodeFetchHandlerEndpointRouteBuilderExtensions
{

    /// <summary>
    /// Builds a <see cref="FetchRequestHandler"/> over the engine pool already registered in the
    /// container, and mounts it.
    /// </summary>
    /// <remarks>
    /// The one-line form, for the common case. It mounts on the fallback alone, because a fetch
    /// handler describes no routes — construct the handler yourself and pair it with an
    /// <see cref="INodeRouteProvider"/> through
    /// <see cref="NodeEndpointRouteBuilderExtensions.MapNode(IEndpointRouteBuilder, INodeRequestHandler, INodeRouteProvider, MapNodeOptions)"/>
    /// for an endpoint per route, or to run on a keyed pool.
    /// </remarks>
    /// <param name="endpoints"></param>
    /// <param name="configure"></param>
    /// <param name="options"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public static IEndpointConventionBuilder MapNodeFetchHandler(this IEndpointRouteBuilder endpoints, Action<FetchRequestHandlerOptions> configure, MapNodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configure);

        var pool = endpoints.ServiceProvider.GetService<NodeEnginePool>()
            ?? throw new InvalidOperationException("No Node engine pool is registered. Add one with AddNodeEnginePool().");

        var handlerOptions = new FetchRequestHandlerOptions();
        configure(handlerOptions);

        var handler = new FetchRequestHandler(pool, handlerOptions, logger: endpoints.ServiceProvider.GetService<ILogger<FetchRequestHandler>>());

        return endpoints.MapNode(handler, null, options);
    }

}
