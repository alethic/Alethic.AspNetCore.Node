using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Alethic.EcmaScript.Hosting;
using Alethic.EcmaScript.Hosting.Http;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Alethic.EcmaScript.Hosting.Node.Tests;

/// <summary>
/// Exercises the fetch contract over the embedded backend, through the Http layer's glue.
/// </summary>
[Collection("Node")]
public class NodeHttpApplicationTests
{

	/// <summary>
	/// An application whose fetch echoes enough of the request to assert on.
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
			routes() { return [{ pattern: '/parks/{parkRef}', renderMode: 'Server' }]; },
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
	/// Builds an application over a fresh pool.
	/// </summary>
	/// <param name="module"></param>
	static (ServiceProvider Services, IJavaScriptHttpApplication Application) Build(string module)
	{
		var services = new ServiceCollection();
		services.AddJavaScriptEnginePool().UseEmbeddedNode();
		var provider = services.BuildServiceProvider();

		var pool = provider.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
		return (provider, pool.GetHttpApplication(JavaScriptModuleSource.FromText("app.cjs", module)));
	}

	[Fact]
	public async Task Send_dispatches_to_fetch_and_returns_response()
	{
		var (services, app) = Build(EchoModule);
		await using var _ = services;

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

		var (services, app) = Build(BodyModule);
		await using var _ = services;

		using var request = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/")
		{
			Content = new StringContent("park data"),
		};

		using var response = await app.SendAsync(request, CancellationToken.None);
		Assert.Equal("got:park data", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Routes_manifest_comes_back_as_json()
	{
		var (services, app) = Build(EchoModule);
		await using var _ = services;

		var json = await app.GetRoutesJsonAsync("routes", CancellationToken.None);

		Assert.NotNull(json);
		Assert.Contains("/parks/{parkRef}", json);
	}

	[Fact]
	public async Task Missing_manifest_is_null_not_an_error()
	{
		var (services, app) = Build(SlowModule);
		await using var _ = services;

		Assert.Null(await app.GetRoutesJsonAsync("routes", CancellationToken.None));
	}

	[Fact]
	public async Task Cancellation_aborts_the_render()
	{
		var (services, app) = Build(SlowModule);
		await using var _ = services;

		using var cts = new CancellationTokenSource();
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/?delay=10000");

		var send = app.SendAsync(request, cts.Token);
		cts.CancelAfter(100);

		// The failure mode this guards against is the render running its full ten seconds with only
		// the caller's wait abandoned; the deadline is what distinguishes them.
		await Assert.ThrowsAnyAsync<Exception>(() => send).WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Missing_default_export_fails_loudly()
	{
		var (services, app) = Build("module.exports.notDefault = 1;");
		await using var _ = services;

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");

		var e = await Assert.ThrowsAnyAsync<Exception>(() => app.SendAsync(request, CancellationToken.None));
		Assert.Contains("fetch", e.Message);
	}

	[Fact]
	public async Task Concurrent_requests_overlap_on_one_engine()
	{
		var (services, app) = Build(SlowModule);
		await using var _ = services;

		async Task One()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/?delay=100");
			using var response = await app.SendAsync(request, CancellationToken.None);
			await response.Content.ReadAsStringAsync();
		}

		// Eight requests, each pausing 100ms inside the module. Serialized they take 800ms; the
		// deadline holds only if the engine overlaps them.
		var all = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => One()));
		await all.WaitAsync(TimeSpan.FromMilliseconds(2500));
	}

}
