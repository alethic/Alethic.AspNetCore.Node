using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Alethic.AspNetCore.Node;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.AspNetCore.Node.Tests;

/// <summary>
/// Drives the endpoint layer through a real server, against a real request handler.
/// </summary>
[TestClass]
public class NodeEndpointTests
{

    /// <summary>
    /// An application carrying its own router — the table it would dispatch on — plus a fetch that
    /// reports what it saw, so tests can assert routing decisions from the outside.
    /// </summary>
    const string AppModule = """
        module.exports.default = {
            router: [
                { id: 'park', path: '/parks/:parkRef', render: 'server' },
                { id: 'profile', path: '/profile', render: 'client' },
                { id: 'about', path: '/about', render: 'prerender' },
            ],
            fetch(request) {
                const url = new URL(request.url);
                if (url.pathname === '/broken')
                    throw new Error('deliberately broken');
                return new Response(
                    '<h1>rendered ' + url.pathname + '</h1>',
                    { status: url.pathname === '/missing' ? 404 : 200,
                      headers: { 'content-type': 'text/html; charset=utf-8', 'x-app': 'yes' } });
            },
        };
        """;

    /// <summary>
    /// An application that reports back what the host told it about its own address.
    /// </summary>
    const string EchoModule = """
        module.exports.default = {
            fetch(request) {
                const saw = n => request.headers.get(n) ?? '(absent)';
                return new Response('ok', {
                    status: 200,
                    headers: {
                        'x-saw-proto': saw('x-forwarded-proto'),
                        'x-saw-host': saw('x-forwarded-host'),
                        'x-saw-prefix': saw('x-forwarded-prefix'),
                        'x-saw-url': request.url,
                    },
                });
            },
        };
        """;

    /// <summary>
    /// An application with no router at all.
    /// </summary>
    const string BareModule = """
        module.exports.default = {
            fetch(request) {
                return new Response('bare', { status: 200 });
            },
        };
        """;

    /// <summary>
    /// Stands up a test server with the module mounted, and hands back the client and the mounted
    /// routes.
    /// </summary>
    /// <param name="module"></param>
    /// <param name="configure"></param>
    /// <param name="pathBase"></param>
    /// <param name="forwardedHeaders"></param>
    static async Task<(WebApplication App, HttpClient Client, IReadOnlyList<RenderRoute> Routes)> StartAsync(string module, Action<MapNodeOptions>? configure = null, string? pathBase = null, bool forwardedHeaders = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNodeEnginePool();

        if (forwardedHeaders)
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.All;

                // The test server gives a request no remote address, which the default known-proxy
                // check reads as untrusted.
                o.KnownNetworks.Clear();
                o.KnownProxies.Clear();
            });

        var app = builder.Build();

        // Ahead of routing, which therefore has to be asked for: minimal hosting otherwise inserts
        // routing before every middleware, and the prefix would still be on the path when a route is
        // matched.
        if (forwardedHeaders || pathBase is not null)
        {
            if (forwardedHeaders)
                app.UseForwardedHeaders();

            if (pathBase is not null)
                app.UsePathBase(pathBase);

            app.UseRouting();
        }

        // A stand-in for "whatever already serves the shell": client-mode routes must land here, not
        // on the engine.
        app.MapGet("/profile", () => Results.Text("<div id=\"app\"></div>", "text/html"));

        var options = new MapNodeOptions();
        configure?.Invoke(options);

        // What got mounted is observed the way a host observes it: through the per-route hook. The
        // caller's own hook, where it set one, still runs.
        var mounted = new List<RenderRoute>();
        var configured = options.ConfigureEndpoint;

        options.ConfigureEndpoint = (route, builder) =>
        {
            if (route is not null)
                mounted.Add(route);

            configured?.Invoke(route, builder);
        };

        var pool = app.Services.GetRequiredService<NodeEnginePool>();
        var source = TestModules.FromText("app.cjs", module);

        app.MapNode(
            new FetchRequestHandler(pool, new FetchRequestHandlerOptions() { Module = source }),
            new RouterRouteProvider(pool, source),
            options);

        await app.StartAsync();
        return (app, app.GetTestClient(), mounted);
    }

    /// <summary>
    /// Reads routes off the application's own <c>router</c> table, standing in for a provider written
    /// against a particular framework. Nothing about rendering appears in it.
    /// </summary>
    sealed class RouterRouteProvider : INodeRouteProvider
    {

        readonly NodeEnginePool pool;
        readonly NodeModuleSource module;

        public RouterRouteProvider(NodeEnginePool pool, NodeModuleSource module)
        {
            this.pool = pool;
            this.module = module;
        }

        public Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken cancellationToken = default) =>
            pool.RunAsync(module, exports =>
            {
                var table = NodeModuleExports.Default(exports)["router"];
                if (table.IsNullOrUndefined())
                    return Task.FromResult<IReadOnlyList<RenderRoute>>([]);

                var routes = new List<RenderRoute>();
                var length = (int)table["length"];

                for (var i = 0; i < length; i++)
                {
                    var entry = table[i];
                    var render = entry["render"];

                    routes.Add(new RenderRoute()
                    {
                        Pattern = (string)entry["path"],
                        Id = (string)entry["id"],
                        RenderMode = render.IsNullOrUndefined() == false && Enum.TryParse<RenderMode>((string)render, ignoreCase: true, out var parsed)
                            ? parsed
                            : RenderMode.Server,
                    });
                }

                return Task.FromResult<IReadOnlyList<RenderRoute>>(routes);
            }, cancellationToken);

    }

    [TestMethod]
    public async Task Router_routes_are_mapped_and_render()
    {
        var (app, client, routes) = await StartAsync(AppModule);
        await using var _ = app;

        // Two of the router's three: the Client route is read but never mapped.
        Assert.AreEqual(2, routes.Count);
        CollectionAssert.AreEquivalent(new[] { "park", "about" }, routes.Select(r => r.Id).ToArray());

        var response = await client.GetAsync("/parks/enchanted-rock");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("yes", response.Headers.GetValues("x-app").Single());
        Assert.AreEqual("<h1>rendered /parks/enchanted-rock</h1>", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Client_routes_never_reach_the_engine()
    {
        var (app, client, _) = await StartAsync(AppModule);
        await using var _1 = app;

        var response = await client.GetAsync("/profile");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("x-app"), "the engine rendered a route the router marked Client");
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "id=\"app\"");
    }

    [TestMethod]
    public async Task Fallback_serves_paths_no_route_claimed()
    {
        var (app, client, _) = await StartAsync(AppModule);
        await using var _1 = app;

        var response = await client.GetAsync("/anything/else");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("<h1>rendered /anything/else</h1>", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Application_status_codes_pass_through()
    {
        var (app, client, _) = await StartAsync(AppModule);
        await using var _1 = app;

        // The application's router decides what exists; a miss must be a real 404 on the wire, not a
        // soft 200 with not-found copy in it.
        var response = await client.GetAsync("/missing");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task No_router_means_fallback_only()
    {
        var (app, client, routes) = await StartAsync(BareModule);
        await using var _ = app;

        // The provider found no router and said so with an empty list, which is a legitimate answer:
        // the application is simply served whole from the fallback.
        Assert.AreEqual(0, routes.Count);
        Assert.AreEqual("bare", await client.GetStringAsync("/whatever"));
    }

    [TestMethod]
    public async Task Application_is_told_the_address_the_caller_used()
    {
        var (app, client, _) = await StartAsync(EchoModule);
        await using var _1 = app;

        var response = await client.GetAsync("/anything");

        Assert.AreEqual("http", response.Headers.GetValues("x-saw-proto").Single());
        Assert.AreEqual("localhost", response.Headers.GetValues("x-saw-host").Single());

        // Absent rather than empty at the root, which is what a proxy rewriting no prefix sends.
        Assert.AreEqual("(absent)", response.Headers.GetValues("x-saw-prefix").Single());
    }

    [TestMethod]
    public async Task Application_is_told_where_it_is_mounted()
    {
        var (app, client, _) = await StartAsync(EchoModule, pathBase: "/store");
        await using var _1 = app;

        var response = await client.GetAsync("/store/cart");

        Assert.AreEqual("/store", response.Headers.GetValues("x-saw-prefix").Single());

        // The path below the mount, and only that: the prefix is removed rather than repeated, which
        // is what X-Forwarded-Prefix means everywhere it is read. The authority is not the caller's
        // — joined to the remaining path it would name somewhere nobody asked for.
        Assert.AreEqual("http://node.invalid/cart", response.Headers.GetValues("x-saw-url").Single());
    }

    [TestMethod]
    public async Task An_application_routes_under_a_mount_knowing_nothing_of_it()
    {
        var (app, client, _) = await StartAsync(AppModule, pathBase: "/store");
        await using var _1 = app;

        var response = await client.GetAsync("/store/parks/enchanted-rock");

        // This application's router declares /parks/:parkRef and nothing about /store. It matches
        // because it is asked for the path below the mount — handed the mounted path it could only
        // read /store as a route and miss, having no way to know it was not one.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("<h1>rendered /parks/enchanted-rock</h1>", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Mounts_accumulate_through_a_chain()
    {
        var (app, client, _) = await StartAsync(EchoModule, pathBase: "/bar", forwardedHeaders: true);
        await using var _1 = app;

        var request = new HttpRequestMessage(HttpMethod.Get, "/bar/blah");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/foo");

        var response = await client.SendAsync(request);

        // A proxy in front publishes the application under /foo and forwards what is below that;
        // this host mounts it again at /bar. Every layer takes its own prefix off the front and adds
        // it to the header, so the application is asked for the path below the innermost mount and
        // the header names each layer above it, outermost first — and proto://host + prefix + path
        // reassembles the address the caller actually used.
        Assert.AreEqual("/foo/bar", response.Headers.GetValues("x-saw-prefix").Single());
        Assert.AreEqual("http://node.invalid/blah", response.Headers.GetValues("x-saw-url").Single());
    }

    [TestMethod]
    public async Task The_request_url_is_not_the_callers_address()
    {
        var (app, client, _) = await StartAsync(EchoModule, pathBase: "/store");
        await using var _1 = app;

        var response = await client.GetAsync("/store/cart");

        // An authority that cannot resolve, so an application cannot quietly take the request URL
        // for where the caller was and emit a link nobody can follow. The headers are the account of
        // that, and reassembling them gives the address actually asked for.
        var url = new Uri(response.Headers.GetValues("x-saw-url").Single());
        Assert.AreEqual("node.invalid", url.Host);

        var proto = response.Headers.GetValues("x-saw-proto").Single();
        var host = response.Headers.GetValues("x-saw-host").Single();
        var prefix = response.Headers.GetValues("x-saw-prefix").Single();

        Assert.AreEqual("http://localhost/store/cart", $"{proto}://{host}{prefix}{url.AbsolutePath}");
    }

    [TestMethod]
    public async Task A_caller_cannot_state_the_mount_itself()
    {
        var (app, client, _) = await StartAsync(EchoModule);
        await using var _1 = app;

        var request = new HttpRequestMessage(HttpMethod.Get, "/anything");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "elsewhere.example");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/elsewhere");

        var response = await client.SendAsync(request);

        // Every header on the request is copied onward, so what a caller sends under these names
        // would otherwise reach the application as though this host had said it.
        Assert.AreEqual("http", response.Headers.GetValues("x-saw-proto").Single());
        Assert.AreEqual("localhost", response.Headers.GetValues("x-saw-host").Single());
        Assert.AreEqual("(absent)", response.Headers.GetValues("x-saw-prefix").Single());
    }

    [TestMethod]
    public async Task Mounting_without_a_provider_is_fallback_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddNodeEnginePool();

        var app = builder.Build();

        // No provider passed. The fetch convention describes no routes, so nothing is mapped per
        // route and the whole application comes off the fallback.
        app.MapNodeFetchHandler(o => o.Module = TestModules.FromText("app.cjs", BareModule));

        await app.StartAsync();
        await using var _ = app;

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.IsFalse(endpoints.Any(e => e.Metadata.GetMetadata<RenderRoute>() is not null));
        Assert.AreEqual("bare", await app.GetTestClient().GetStringAsync("/whatever"));
    }

    [TestMethod]
    public async Task A_provider_that_throws_fails_startup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        await using var _ = app;

        // A broken extraction is a bug, not an absence. It must not pass for an application that
        // simply has no routes, which is what the old catch-all made it look like.
        var e = Assert.ThrowsExactly<InvalidOperationException>(
            () => app.MapNode(new StubRenderer(), new ThrowingRouteProvider()));

        StringAssert.Contains(e.Message, "router is broken");
    }

    [TestMethod]
    public async Task ConfigureEndpoint_sees_each_route_and_the_fallback()
    {
        var seen = new List<string?>();

        var (app, _, _) = await StartAsync(AppModule, o => o.ConfigureEndpoint = (route, _) => seen.Add(route?.Id));
        await using var _1 = app;

        // Client routes are not mapped, so the callback must not see them either; null is the fallback.
        CollectionAssert.AreEqual(new string?[] { "park", "about", null }, seen);
    }

    [TestMethod]
    public async Task Route_ids_name_endpoints_for_LinkGenerator()
    {
        var (app, _, _) = await StartAsync(AppModule);
        await using var _1 = app;

        // The route's id names the endpoint, so ASP.NET's own LinkGenerator builds URLs by route
        // name — canonical redirects and sitemaps use the platform facility, not a bespoke one.
        var links = app.Services.GetRequiredService<LinkGenerator>();

        Assert.AreEqual("/parks/enchanted-rock", links.GetPathByName("park", new { parkRef = "enchanted-rock" }));
        Assert.AreEqual("/about", links.GetPathByName("about", values: null));
    }

    [TestMethod]
    public async Task Render_failure_surfaces_as_an_error()
    {
        var (app, client, _) = await StartAsync(AppModule);
        await using var _1 = app;

        // A failure before the first byte propagates like any unhandled endpoint exception, for the
        // server's error handling to turn into a 500. The test host rethrows instead of translating,
        // which is what makes the contract assertable here.
        var e = await Assert.ThrowsAsync<Exception>(() => client.GetAsync("/broken"));
        StringAssert.Contains(e.Message, "deliberately broken");
    }

    [TestMethod]
    public async Task Any_implementation_of_the_interface_mounts_the_same_way()
    {
        // The reason the interface survives: an application speaking some protocol other than the
        // fetch convention supplies its own handler and mounts identically. Nothing in the endpoint
        // layer knows which one it has, or that no Node module is behind this one at all.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();

        // Mounting hands back one convention builder over everything it produced, the way every other
        // Map method does, so host policy applies to the lot in one statement.
        app.MapNode(new StubRenderer(), new StubRouteProvider()).WithMetadata(new Marker());

        await app.StartAsync();
        await using var _ = app;

        Assert.AreEqual("stubbed /parks/enchanted-rock", await app.GetTestClient().GetStringAsync("/parks/enchanted-rock"));

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        // The route endpoint and the fallback alike.
        Assert.AreEqual(2, endpoints.Count(e => e.Metadata.GetMetadata<Marker>() is not null));

        // And the route rides along as metadata, which is how the host enumerates what was mapped
        // without the mapping method handing back a list of its own.
        Assert.AreEqual("stub", endpoints.Select(e => e.Metadata.GetMetadata<RenderRoute>()).Single(r => r is not null)!.Id);
    }

    /// <summary>
    /// Marks endpoints, to prove a convention reached every one of them.
    /// </summary>
    sealed class Marker
    {
    }

    /// <summary>
    /// A handler with no Node module behind it at all, standing in for an implementation speaking a
    /// framework's own server protocol.
    /// </summary>
    sealed class StubRenderer : INodeRequestHandler
    {

        public Task PrepareAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"stubbed {request.RequestUri?.AbsolutePath}"),
            });

    }

    /// <summary>
    /// Routes for the stub handler, supplied separately from it.
    /// </summary>
    sealed class StubRouteProvider : INodeRouteProvider
    {

        public Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RenderRoute>>([new RenderRoute() { Pattern = "/parks/:parkRef", Id = "stub" }]);

    }

    /// <summary>
    /// A route provider whose extraction fails, as one written against the wrong framework would.
    /// </summary>
    sealed class ThrowingRouteProvider : INodeRouteProvider
    {

        public Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the application's router is broken");

    }

}
