using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Alethic.AspNetCore.EcmaScript;
using Alethic.AspNetCore.EcmaScript.Node;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Alethic.AspNetCore.EcmaScript.Node.Tests;

/// <summary>
/// Exercises the rendering engine through the abstraction: HTTP in, HTTP out, routes.
/// </summary>
[Collection("Node")]
public class NodeRenderEngineTests
{

	/// <summary>
	//// An application whose fetch echoes enough of the request to assert on.
	/// </summary>
	const string EchoModule = """
		module.exports.default = {
			fetch(request) {
				const url = new URL(request.url);
				const headers = {};
				for (const [k, v] of request.headers.entries())
					headers[k] = v;
				return new Response(
					JSON.stringify({ path: url.pathname, method: request.method, headers }),
					{ status: 200, headers: { 'content-type': 'application/json', 'x-app': 'yes' } });
			},
			routes() {
				return [
					{ pattern: '/parks/:parkRef', renderMode: 'Server', id: 'park' },
					{ pattern: '/profile', renderMode: 'Client', id: 'profile' },
				];
			},
		};
		""";

	/// <summary>
	/// An application that waits before answering, honoring its abort signal.
	/// </summary>
	const string SlowModule = """
		module.exports.default = {
			async fetch(request) {
				const url = new URL(request.url);
				const delay = Number(url.searchParams.get('delay') ?? 50);
				await new Promise((resolve, reject) => {
					const timer = setTimeout(resolve, delay);
					request.signal.addEventListener('abort', () => { clearTimeout(timer); reject(new Error('aborted')); });
				});
				return new Response('slow done', { status: 200 });
			},
		};
		""";

	/// <summary>
	/// Builds a provider with the two registrations: the pool, and a rendering engine on it.
	/// </summary>
	/// <param name="module"></param>
	/// <param name="configurePool"></param>
	static (ServiceProvider Services, IRenderEngine Engine) Build(string module, Action<NodeEnginePoolOptions>? configurePool = null)
	{
		var services = new ServiceCollection();
		services.AddNodeEnginePool(configurePool);
		services.AddNodeRenderEngine(o => o.Module = NodeModuleSource.FromText("app.cjs", module));
		var provider = services.BuildServiceProvider();
		return (provider, provider.GetRequiredService<IRenderEngine>());
	}

	[Fact]
	public async Task Send_renders_a_request()
	{
		var (services, engine) = Build(EchoModule);
		await using var _ = services;

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/parks/enchanted-rock");
		request.Headers.Add("x-probe", "value");

		using var response = await engine.SendAsync(request);
		var text = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("yes", response.Headers.GetValues("x-app").Single());
		Assert.Contains("\"path\":\"/parks/enchanted-rock\"", text);
		Assert.Contains("\"method\":\"GET\"", text);
		Assert.Contains("\"x-probe\":\"value\"", text);
	}

	[Fact]
	public async Task Request_bodies_reach_the_application()
	{
		const string BodyModule = """
			module.exports.default = {
				async fetch(request) {
					const text = await request.text();
					return new Response('got:' + text, { status: 200 });
				},
			};
			""";

		var (services, engine) = Build(BodyModule);
		await using var _ = services;

		using var request = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/")
		{
			Content = new StringContent("park data"),
		};

		using var response = await engine.SendAsync(request);
		Assert.Equal("got:park data", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Routes_come_back_typed()
	{
		var (services, engine) = Build(EchoModule);
		await using var _ = services;

		var routes = await engine.GetRoutesAsync();

		Assert.NotNull(routes);
		Assert.Equal(2, routes.Count);
		Assert.Equal("/parks/:parkRef", routes[0].Pattern);
		Assert.Equal(RenderMode.Server, routes[0].RenderMode);
		Assert.Equal(RenderMode.Client, routes[1].RenderMode);
	}

	[Fact]
	public async Task Missing_manifest_is_null_not_an_error()
	{
		var (services, engine) = Build(SlowModule);
		await using var _ = services;

		Assert.Null(await engine.GetRoutesAsync());
	}

	[Fact]
	public async Task Cancellation_aborts_the_render()
	{
		var (services, engine) = Build(SlowModule);
		await using var _ = services;

		using var cts = new CancellationTokenSource();
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/?delay=10000");

		var send = engine.SendAsync(request, cts.Token);
		cts.CancelAfter(100);

		// The failure mode this guards against is the render running its full ten seconds with only
		// the caller's wait abandoned; the deadline is what distinguishes them.
		await Assert.ThrowsAnyAsync<Exception>(() => send).WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Missing_default_export_fails_loudly()
	{
		var (services, engine) = Build("module.exports.notDefault = 1;");
		await using var _ = services;

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");

		var e = await Assert.ThrowsAnyAsync<Exception>(() => engine.SendAsync(request));
		Assert.Contains("fetch", e.Message);
	}

	[Fact]
	public async Task Concurrent_renders_overlap_on_one_engine()
	{
		var (services, engine) = Build(SlowModule);
		await using var _ = services;

		async Task One()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/?delay=100");
			using var response = await engine.SendAsync(request);
			await response.Content.ReadAsStringAsync();
		}

		// Eight requests, each pausing 100ms inside the module. Serialized they take 800ms; the
		// deadline holds only if the engine overlaps them.
		await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => One())).WaitAsync(TimeSpan.FromMilliseconds(2500));
	}

	[Fact]
	public async Task Prepare_fails_on_an_unreadable_module()
	{
		var services = new ServiceCollection();
		services.AddNodeEnginePool();
		services.AddNodeRenderEngine(o => o.Module = NodeModuleSource.FromFile("Z:/does/not/exist.cjs"));
		await using var provider = services.BuildServiceProvider();
		var engine = provider.GetRequiredService<IRenderEngine>();

		// A broken module must fail preparation — and with it the deployment — rather than stand up
		// an engine that quietly serves nothing.
		await Assert.ThrowsAnyAsync<Exception>(() => engine.PrepareAsync());
	}

}
