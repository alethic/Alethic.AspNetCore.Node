using System;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.AspNetCore.EcmaScript.Node.Tests;

/// <summary>
/// Exercises the entry contract's optional shapes: a bare function as the default export, the
/// once-per-engine init, and the host-supplied environment.
/// </summary>
[TestClass]
public class NodeRenderEngineContractTests
{

	/// <summary>
	/// Builds a provider with the two registrations and hands back the engine.
	/// </summary>
	/// <param name="module"></param>
	/// <param name="configure"></param>
	static (ServiceProvider Services, IRenderEngine Engine) Build(string module, Action<NodeRenderEngineOptions>? configure = null)
	{
		var services = new ServiceCollection();
		services.AddNodeEnginePool();
		services.AddNodeRenderEngine(o =>
		{
			o.Module = NodeModuleSource.FromText("app.cjs", module);
			configure?.Invoke(o);
		});
		var provider = services.BuildServiceProvider();
		return (provider, provider.GetRequiredService<IRenderEngine>());
	}

	[TestMethod]
	public async Task A_bare_function_default_is_the_fetch_handler()
	{
		// What createRequestHandler-style factories produce: the handler itself, not an object
		// carrying one.
		const string Module = """
			module.exports.default = (request) => new Response('bare:' + new URL(request.url).pathname, { status: 200 });
			""";

		var (services, engine) = Build(Module);
		await using var _ = services;

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/x");
		using var response = await engine.SendAsync(request);

		Assert.AreEqual("bare:/x", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task Init_runs_once_per_engine_before_anything_else()
	{
		// A CommonJS bundle cannot top-level-await, so asynchronous startup work lives in init. The
		// counter proves it ran, exactly once, and before the first render observed its result.
		const string Module = """
			let ready = 'no';
			let initCalls = 0;
			module.exports.default = {
				async init() {
					initCalls++;
					await new Promise(r => setTimeout(r, 10));
					ready = 'yes';
				},
				fetch(request) {
					return new Response(ready + ':' + initCalls, { status: 200 });
				},
			};
			""";

		var (services, engine) = Build(Module);
		await using var _ = services;

		async Task<string> One()
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");
			using var response = await engine.SendAsync(request);
			return await response.Content.ReadAsStringAsync();
		}

		Assert.AreEqual("yes:1", await One());
		Assert.AreEqual("yes:1", await One());
	}

	[TestMethod]
	public async Task Environment_reaches_fetch_and_init()
	{
		// The module-worker convention's env argument: the place for what only the host knows.
		const string Module = """
			let fromInit = '';
			module.exports.default = {
				init(env) {
					fromInit = env.ApiBaseUri;
				},
				fetch(request, env) {
					return new Response(fromInit + '|' + env.ApiBaseUri + '|' + env.Name, { status: 200 });
				},
			};
			""";

		var (services, engine) = Build(Module, o =>
		{
			o.Environment["ApiBaseUri"] = "http://api.internal:8080/";
			o.Environment["Name"] = "unit";
		});
		await using var _ = services;

		using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");
		using var response = await engine.SendAsync(request);

		Assert.AreEqual("http://api.internal:8080/|http://api.internal:8080/|unit", await response.Content.ReadAsStringAsync());
	}

	[TestMethod]
	public async Task A_failing_init_fails_preparation()
	{
		const string Module = """
			module.exports.default = {
				init() { throw new Error('deliberately unprepared'); },
				fetch(request) { return new Response('never', { status: 200 }); },
			};
			""";

		var (services, engine) = Build(Module);
		await using var _ = services;

		// A half-started application must fail the deployment, not quietly serve.
		var e = await Assert.ThrowsAsync<Exception>(() => engine.PrepareAsync());
		StringAssert.Contains(e.Message, "deliberately unprepared");
	}

	[TestMethod]
	public async Task A_function_default_can_still_carry_the_manifest()
	{
		// Functions are objects, so a wrapper can hang routes off the handler it returns.
		const string Module = """
			const handler = (request) => new Response('ok', { status: 200 });
			handler.routes = () => [{ pattern: '/x/:id', renderMode: 'Server', id: 'x' }];
			module.exports.default = handler;
			""";

		var (services, engine) = Build(Module);
		await using var _ = services;

		var routes = await engine.GetRoutesAsync();

		Assert.IsNotNull(routes);
		Assert.AreEqual(1, routes.Count);
		Assert.AreEqual("/x/:id", routes[0].Pattern);
	}

}
