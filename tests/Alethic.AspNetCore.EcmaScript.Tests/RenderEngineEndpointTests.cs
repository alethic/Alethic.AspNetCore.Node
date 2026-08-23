using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Alethic.AspNetCore.EcmaScript;
using Alethic.AspNetCore.EcmaScript.Node;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Alethic.AspNetCore.EcmaScript.Tests;

/// <summary>
/// Drives the endpoint layer through a real server, against the Node rendering engine.
/// </summary>
[Collection("Node")]
public class RenderEngineEndpointTests
{

	/// <summary>
	/// An application with a manifest: server routes, a client route, and a fetch that reports what
	/// it saw so tests can assert on routing decisions from the outside.
	/// </summary>
	const string AppModule = """
		module.exports.default = {
			fetch(request) {
				const url = new URL(request.url);
				if (url.pathname === '/broken')
					throw new Error('deliberately broken');
				return new Response(
					'<h1>rendered ' + url.pathname + '</h1>',
					{ status: url.pathname === '/missing' ? 404 : 200,
					  headers: { 'content-type': 'text/html; charset=utf-8', 'x-app': 'yes' } });
			},
			routes() {
				return [
					{ pattern: '/parks/{parkRef}', renderMode: 'Server', id: 'park' },
					{ pattern: '/profile', renderMode: 'Client', id: 'profile' },
					{ pattern: '/about', renderMode: 'Prerender', id: 'about' },
				];
			},
		};
		""";

	/// <summary>
	/// An application with no manifest at all.
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
	static async Task<(WebApplication App, HttpClient Client, IReadOnlyList<RenderRoute> Routes)> StartAsync(string module, Action<MapRenderEngineOptions>? configure = null)
	{
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddNodeEnginePool();
		builder.Services.AddNodeRenderEngine(o => o.Module = NodeModuleSource.FromText("app.cjs", module));

		var app = builder.Build();

		// A stand-in for "whatever already serves the shell": client-mode routes must land here, not
		// on the engine.
		app.MapGet("/profile", () => Results.Text("<div id=\"app\"></div>", "text/html"));

		var options = new MapRenderEngineOptions();
		configure?.Invoke(options);
		var routes = await app.MapRenderEngineAsync(options);

		await app.StartAsync();
		return (app, app.GetTestClient(), routes);
	}

	[Fact]
	public async Task Manifest_routes_are_mapped_and_render()
	{
		var (app, client, routes) = await StartAsync(AppModule);
		await using var _ = app;

		Assert.Equal(3, routes.Count);

		var response = await client.GetAsync("/parks/enchanted-rock");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("yes", response.Headers.GetValues("x-app").Single());
		Assert.Equal("<h1>rendered /parks/enchanted-rock</h1>", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Client_routes_never_reach_the_engine()
	{
		var (app, client, _) = await StartAsync(AppModule);
		await using var _1 = app;

		var response = await client.GetAsync("/profile");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.False(response.Headers.Contains("x-app"), "the engine rendered a route the manifest marked Client");
		Assert.Contains("id=\"app\"", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Fallback_serves_paths_no_route_claimed()
	{
		var (app, client, _) = await StartAsync(AppModule);
		await using var _1 = app;

		var response = await client.GetAsync("/anything/else");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("<h1>rendered /anything/else</h1>", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Application_status_codes_pass_through()
	{
		var (app, client, _) = await StartAsync(AppModule);
		await using var _1 = app;

		// The application's router decides what exists; a miss must be a real 404 on the wire, not a
		// soft 200 with not-found copy in it.
		var response = await client.GetAsync("/missing");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task No_manifest_means_fallback_only()
	{
		var (app, client, routes) = await StartAsync(BareModule);
		await using var _ = app;

		Assert.Empty(routes);
		Assert.Equal("bare", await client.GetStringAsync("/whatever"));
	}

	[Fact]
	public async Task Missing_manifest_fails_startup_when_required()
	{
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => StartAsync(BareModule, o => o.RequireManifest = true));
	}

	[Fact]
	public async Task ConfigureEndpoint_sees_each_route_and_the_fallback()
	{
		var seen = new List<string?>();

		var (app, _, _) = await StartAsync(AppModule, o => o.ConfigureEndpoint = (route, _) => seen.Add(route?.Id));
		await using var _1 = app;

		// Client routes are not mapped, so the callback must not see them either; null is the fallback.
		Assert.Equal(["park", "about", null], seen);
	}

	[Fact]
	public async Task Manifest_ids_name_endpoints_for_LinkGenerator()
	{
		var (app, _, _) = await StartAsync(AppModule);
		await using var _1 = app;

		// The manifest's id names the endpoint, so ASP.NET's own LinkGenerator builds URLs by route
		// name — canonical redirects and sitemaps use the platform facility, not a bespoke one.
		var links = app.Services.GetRequiredService<LinkGenerator>();

		Assert.Equal("/parks/enchanted-rock", links.GetPathByName("park", new { parkRef = "enchanted-rock" }));
		Assert.Equal("/about", links.GetPathByName("about", values: null));
	}

	[Fact]
	public async Task Render_failure_surfaces_as_an_error()
	{
		var (app, client, _) = await StartAsync(AppModule);
		await using var _1 = app;

		// A failure before the first byte propagates like any unhandled endpoint exception, for the
		// server's error handling to turn into a 500. The test host rethrows instead of translating,
		// which is what makes the contract assertable here.
		var e = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/broken"));
		Assert.Contains("deliberately broken", e.Message);
	}

}

/// <summary>
/// Serializes every test that touches the embedded runtime.
/// </summary>
[CollectionDefinition("Node", DisableParallelization = true)]
public class NodeCollection
{

}
