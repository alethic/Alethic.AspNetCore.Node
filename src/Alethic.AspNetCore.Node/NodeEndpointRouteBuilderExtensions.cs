using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Alethic.AspNetCore.Node;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Mounts Node handlers into the endpoint table.
/// </summary>
/// <remarks>
/// Written against <see cref="INodeRequestHandler"/> rather than any one implementation, so an
/// application speaking a framework's own server protocol mounts exactly as one behind a
/// <c>fetch</c> handler does.
/// </remarks>
public static class NodeEndpointRouteBuilderExtensions
{

    /// <summary>
    /// Maps a single pattern to a handler.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="pattern"></param>
    /// <param name="handler"></param>
    public static IEndpointConventionBuilder MapNode(this IEndpointRouteBuilder endpoints, string pattern, INodeRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);

        return endpoints.Map(pattern, context => handler.HandleAsync(context));
    }

    /// <summary>
    /// Mounts a handler: prepares it, reads the application's routes where a provider is given, and maps an
    /// endpoint per route plus a fallback.
    /// </summary>
    /// <remarks>
    /// Takes the handler rather than resolving one, because a handler is a small object over a
    /// pool and the pool is the part with a lifetime worth registering. Any
    /// <see cref="INodeRequestHandler"/> mounts the same way, whatever protocol it speaks to the
    /// application behind it; pair it with an <see cref="INodeRouteProvider"/> for an endpoint per
    /// route, or omit one and the application is served entirely from the fallback.
    ///
    /// Synchronous on purpose: endpoint configuration is synchronous by shape, so this method owns
    /// the wait rather than making every caller block on a task themselves. The engine prepares and
    /// the routes are read before the server starts — the route table is read from the running
    /// application rather than a copy that could drift, and the preparation cost lands at startup
    /// instead of under the first request. A handler that cannot prepare fails the deployment here,
    /// not by quietly serving nothing.
    ///
    /// Routes marked <see cref="RenderMode.Client"/> are not mapped. Whatever already serves the
    /// application shell — static assets, a fallback view — keeps serving them, and the engine is
    /// never invoked on their behalf.
    ///
    /// Returns a convention builder over every endpoint mounted, the fallback included, so policy
    /// applies to the lot at once. Policy that varies by route belongs in
    /// <see cref="MapNodeOptions.ConfigureEndpoint"/>, which sees the route; and the
    /// routes themselves are carried as endpoint metadata, so
    /// <see cref="Microsoft.AspNetCore.Routing.EndpointDataSource"/> enumerates what was mapped.
    /// </remarks>
    /// <param name="endpoints"></param>
    /// <param name="handler"></param>
    /// <param name="routes">Reads the application's routes, or null to serve it wholly from the fallback.</param>
    /// <param name="options"></param>
    public static IEndpointConventionBuilder MapNode(this IEndpointRouteBuilder endpoints, INodeRequestHandler handler, INodeRouteProvider? routes = null, MapNodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new MapNodeOptions();

        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Alethic.AspNetCore.Node");

        // no synchronization context exists during endpoint configuration, so blocking here cannot deadlock
        handler.PrepareAsync().GetAwaiter().GetResult();

        // Not caught: a provider that cannot read the router it was written for is broken, and that
        // must fail the deployment rather than pass for an application that simply has no routes.
        var mapped = routes is null
            ? []
            : routes.GetRoutesAsync(CancellationToken.None).GetAwaiter().GetResult();

        var mounted = new MountedEndpoints();

        foreach (var route in mapped)
        {
            var template = route.Pattern is null ? null : UrlPatternConverter.ToRouteTemplate(route.Pattern);
            if (template is null)
            {
                // Absent, or using URLPattern features a route template cannot carry: the fallback
                // serves it, and the application's own router still renders the right thing. Only the
                // per-route policy is lost.
                logger.LogDebug("Render route {Id} ({Pattern}) is left to the fallback.", route.Id ?? "(unnamed)", route.Pattern ?? "no pattern");
                continue;
            }

            if (route.RenderMode == RenderMode.Client)
                continue;

            var endpoint = endpoints.Map(template, context => handler.HandleAsync(context));
            endpoint.WithMetadata(route);

            // A route with an id is an addressable endpoint: LinkGenerator can build its URL by name
            // (GetPathByName(id, values)), which is what server-side code uses for canonical
            // redirects, sitemaps, and links — no bespoke URL generator required.
            if (route.Id is not null)
                endpoint.WithName(route.Id);

            options.ConfigureEndpoint?.Invoke(route, endpoint);
            mounted.Include(endpoint);
        }

        if (options.FallbackPattern is not null)
        {
            // Ordered behind everything else so it only answers what nothing more specific claimed,
            // the same way a SPA fallback file does.
            var fallback = endpoints.MapFallback(options.FallbackPattern, context => handler.HandleAsync(context));
            options.ConfigureEndpoint?.Invoke(null, fallback);
            mounted.Include(fallback);
        }

        return mounted;
    }

    /// <summary>
    /// One convention builder over all the endpoints a single mount produced.
    /// </summary>
    /// <remarks>
    /// A mount yields an endpoint per route plus a fallback, where the <c>Map</c> shape promises the
    /// caller one thing to hang conventions on, so each convention is fanned out across the lot.
    /// Conventions added after mounting still land: endpoint builders apply theirs when the endpoint
    /// is built, which happens after configuration completes.
    /// </remarks>
    sealed class MountedEndpoints : IEndpointConventionBuilder
    {

        readonly List<IEndpointConventionBuilder> builders = [];

        /// <summary>
        /// Adds an endpoint to those this builder covers.
        /// </summary>
        /// <param name="builder"></param>
        public void Include(IEndpointConventionBuilder builder) => builders.Add(builder);

        /// <inheritdoc />
        public void Add(Action<EndpointBuilder> convention)
        {
            foreach (var builder in builders)
                builder.Add(convention);
        }

        /// <inheritdoc />
        public void Finally(Action<EndpointBuilder> finallyConvention)
        {
            foreach (var builder in builders)
                builder.Finally(finallyConvention);
        }

    }

}
