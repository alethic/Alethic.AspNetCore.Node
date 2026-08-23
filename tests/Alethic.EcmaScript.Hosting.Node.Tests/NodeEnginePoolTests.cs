using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Alethic.EcmaScript.Hosting;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Alethic.EcmaScript.Hosting.Node.Tests;

/// <summary>
/// Exercises the embedded Node backend through the public surface.
/// </summary>
/// <remarks>
/// One collection, serialized: the embedding platform is per-process, so every test shares it and
/// nothing here may run in parallel with anything that would create a second one.
/// </remarks>
[Collection("Node")]
public class NodeEnginePoolTests
{

	/// <summary>
	/// A module whose fetch export echoes enough of the request to assert on, and whose invoke
	/// exports cover synchronous, asynchronous and void shapes.
	/// </summary>
	const string EchoModule = """
		let calls = 0;
		module.exports.default = {
			fetch(request) {
				calls++;
				const url = new URL(request.url);
				const headers = {};
				for (const [k, v] of request.headers.entries())
					headers[k] = v;
				return new Response(
					JSON.stringify({ path: url.pathname, method: request.method, headers, calls }),
					{ status: 200, headers: { 'content-type': 'application/json', 'x-echo': 'yes' } });
			},
			add(a, b) { return a + b; },
			async addLater(a, b) { return await new Promise(r => setTimeout(() => r(a + b), 10)); },
			nothing() { },
			routes() { return [{ pattern: '/parks/{parkRef}', renderMode: 'Server' }]; },
		};
		""";

	/// <summary>
	/// A module that suspends on a timer before answering, for concurrency and cancellation tests.
	/// </summary>
	const string SlowModule = """
		let inFlight = 0, peak = 0;
		module.exports.default = {
			async fetch(request) {
				inFlight++;
				peak = Math.max(peak, inFlight);
				try {
					const url = new URL(request.url);
					const delay = Number(url.searchParams.get('delay') ?? 50);
					const signal = request.signal;
					await new Promise((resolve, reject) => {
						const timer = setTimeout(resolve, delay);
						signal.addEventListener('abort', () => { clearTimeout(timer); reject(new Error('aborted')); });
					});
					return new Response('slow done', { status: 200, headers: { 'x-peak': String(peak) } });
				}
				finally {
					inFlight--;
				}
			},
		};
		""";

	/// <summary>
	/// Builds a provider with one default pool on the embedded Node backend.
	/// </summary>
	/// <param name="configure"></param>
	static ServiceProvider BuildServices(Action<JavaScriptEnginePoolOptions>? configure = null)
	{
		var services = new ServiceCollection();
		services.AddJavaScriptEnginePool(configure).UseEmbeddedNode();
		return services.BuildServiceProvider();
	}

	[Fact]
	public async Task Send_dispatches_to_fetch_and_returns_response()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var app = pool.GetApplication(JavaScriptModuleSource.FromText("echo.cjs", EchoModule));

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/parks/enchanted-rock");
		request.Headers.Add("x-probe", "value");

		using var response = await app.SendAsync(request, CancellationToken.None);
		var text = await response.Content.ReadAsStringAsync();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("yes", response.Headers.GetValues("x-echo").Single());
		Assert.Contains("\"path\":\"/parks/enchanted-rock\"", text);
		Assert.Contains("\"method\":\"GET\"", text);
		Assert.Contains("\"x-probe\":\"value\"", text);
	}

	[Fact]
	public async Task Invoke_marshals_arguments_and_results_as_json()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var app = pool.GetApplication(JavaScriptModuleSource.FromText("echo.cjs", EchoModule));

		Assert.Equal(5, await app.InvokeAsync<int>("add", [2, 3], CancellationToken.None));
		Assert.Equal(7, await app.InvokeAsync<int>("addLater", [3, 4], CancellationToken.None));
		Assert.Equal(0, await app.InvokeAsync<int>("nothing", [], CancellationToken.None));
	}

	[Fact]
	public async Task Invoke_returns_structured_data()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var app = pool.GetApplication(JavaScriptModuleSource.FromText("echo.cjs", EchoModule));

		var routes = await app.InvokeAsync<List<RouteEntry>>("routes", [], CancellationToken.None);

		Assert.NotNull(routes);
		var route = Assert.Single(routes);
		Assert.Equal("/parks/{parkRef}", route.Pattern);
		Assert.Equal("Server", route.RenderMode);
	}

	[Fact]
	public async Task Concurrent_sends_overlap_on_one_engine()
	{
		await using var services = BuildServices(o => o.MaxConcurrencyPerEngine = 8);
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var app = pool.GetApplication(JavaScriptModuleSource.FromText("slow.cjs", SlowModule));

		async Task<string> One()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/?delay=100");
			using var response = await app.SendAsync(request, CancellationToken.None);
			await response.Content.ReadAsStringAsync();
			return response.Headers.GetValues("x-peak").Single();
		}

		// Eight requests, each pausing 100ms inside the module. If the engine were serialized this
		// takes 800ms and peak in-flight is one; overlapped it takes roughly one delay and the module
		// itself observes the overlap.
		var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => One()));

		Assert.True(results.Select(int.Parse).Max() > 1, "renders never overlapped inside the module");
	}

	[Fact]
	public async Task Cancellation_aborts_the_render()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var app = pool.GetApplication(JavaScriptModuleSource.FromText("slow.cjs", SlowModule));

		using var cts = new CancellationTokenSource();
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/?delay=10000");

		var send = app.SendAsync(request, cts.Token);
		cts.CancelAfter(100);

		// The failure mode this guards against is the render running its full ten seconds and only
		// the caller's wait being abandoned; the deadline is what distinguishes them.
		await Assert.ThrowsAnyAsync<Exception>(() => send).WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Module_state_is_reused_within_an_engine()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var app = pool.GetApplication(JavaScriptModuleSource.FromText("echo.cjs", EchoModule));

		async Task<string> One()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");
			using var response = await app.SendAsync(request, CancellationToken.None);
			return await response.Content.ReadAsStringAsync();
		}

		var first = await One();
		var second = await One();

		// Same engine, same module instance: the counter advances rather than resetting, which is
		// exactly the evaluated-once contract of a module source.
		Assert.Contains("\"calls\":1", first);
		Assert.Contains("\"calls\":2", second);
	}

	[Fact]
	public async Task Missing_default_export_fails_loudly()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		var app = pool.GetApplication(JavaScriptModuleSource.FromText("bare.cjs", "module.exports.notDefault = 1;"));

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");

		var e = await Assert.ThrowsAnyAsync<Exception>(() => app.SendAsync(request, CancellationToken.None));
		Assert.Contains("default export", e.Message);
	}

	/// <summary>
	/// Shape of the route entries the test module returns.
	/// </summary>
	record RouteEntry(
		[property: System.Text.Json.Serialization.JsonPropertyName("pattern")] string Pattern,
		[property: System.Text.Json.Serialization.JsonPropertyName("renderMode")] string RenderMode);

}

/// <summary>
/// Serializes every test that touches the embedded runtime.
/// </summary>
[CollectionDefinition("Node", DisableParallelization = true)]
public class NodeCollection
{

}
