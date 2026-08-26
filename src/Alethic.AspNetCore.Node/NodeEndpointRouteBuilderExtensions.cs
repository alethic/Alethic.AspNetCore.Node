using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Alethic.AspNetCore.Node;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
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

        return endpoints.Map(pattern, context => DispatchAsync(context, handler));
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

            var endpoint = endpoints.Map(template, context => DispatchAsync(context, handler));
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
            var fallback = endpoints.MapFallback(options.FallbackPattern, context => DispatchAsync(context, handler));
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

    /// <summary>
    /// <summary>
    /// Serves one HTTP request from the engine.
    /// </summary>
    /// <remarks>
    /// The response is copied as it is produced, so a page that emits its shell ahead of suspended
    /// content reaches the client that way rather than all at once. The client going away aborts the
    /// render through the request's cancellation, not merely the copy.
    /// </remarks>
    /// <param name="context"></param>
    /// <param name="engine"></param>
    static async Task DispatchAsync(HttpContext context, INodeRequestHandler engine)
    {
        using var request = BuildRequest(context);
        using var response = await engine.SendAsync(request, context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();

        foreach (var header in response.Content.Headers)
        {
            // The body arrives as a stream of unknown length, and the server frames it itself; a
            // length or transfer coding copied from the engine would claim otherwise.
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);

        // Copied chunk by chunk with a flush per chunk, so progress made by the render is progress
        // seen by the client. A plain copy would batch on the server's own buffer instead. The buffer
        // is rented because this runs once per document: a fresh 16KB array per request is 16KB of
        // garbage per request, and at that size it comes from the large-ish end of gen0 every time.
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                var read = await body.ReadAsync(buffer, context.RequestAborted);
                if (read == 0)
                    break;

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        finally
        {
            // Not cleared: the pool hands this to the next render, which overwrites what it reads,
            // and the content is a public document either way.
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Headers describing where the application is mounted, which this host states from the request
    /// it resolved rather than passing on whatever arrived under those names.
    /// </summary>
    static readonly string[] MountHeaders = ["X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix"];

    /// <summary>
    /// Rebuilds the incoming request in the shape the engine expects.
    /// </summary>
    /// <remarks>
    /// The application behind this is an origin server, and this is what stands in front of it, so it
    /// is told where it is mounted the way an origin server behind a proxy is told: the scheme and
    /// authority the caller used, and the prefix the path is served under.
    ///
    /// Two of those are in the URL as well, and are taken from the same place, so the two accounts
    /// cannot come to disagree. The prefix is the one that is not: a path base is merged into the
    /// path with nothing to mark where it ends, leaving <c>/store/cart</c> mounted at <c>/store</c>
    /// indistinguishable from <c>/store/cart</c> mounted at the root. Only this side of the call
    /// knows the difference, so only this side can say.
    /// </remarks>
    /// <param name="context"></param>
    static HttpRequestMessage BuildRequest(HttpContext context)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.GetEncodedUrl());

        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
            request.Content = new StreamContent(context.Request.Body);

        foreach (var header in context.Request.Headers)
        {
            // The authority travels in the URL; a Host header besides it is at best redundant and at
            // worst contradicts a PathBase-adjusted address.
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            // Stated below instead. Passing the arriving value on would let a caller describe the
            // mount to the application and have it read as though this host had said so — and every
            // header here is copied onward, so not copying it is what prevents that.
            if (MountHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                continue;

            if (request.Headers.TryAddWithoutValidation(header.Key, (string?[])header.Value) == false)
                request.Content?.Headers.TryAddWithoutValidation(header.Key, (string?[])header.Value);
        }

        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);

        if (context.Request.Host.HasValue)
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);

        // Absent rather than empty at the root, which is what a proxy rewriting no prefix sends, and
        // which leaves an application's own default as the answer rather than a case to handle.
        if (context.Request.PathBase.HasValue)
            request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", context.Request.PathBase.Value);

        return request;
    }

}
