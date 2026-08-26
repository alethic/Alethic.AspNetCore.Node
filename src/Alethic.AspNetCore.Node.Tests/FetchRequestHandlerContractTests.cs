using System;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.AspNetCore.Node.Tests;

/// <summary>
/// Exercises the entry contract's optional shapes: a bare function as the default export, the
/// module-scope startup, and the host-supplied environment.
/// </summary>
[TestClass]
public class FetchRequestHandlerContractTests
{

    /// <summary>
    /// Registers a pool, builds an application on it, and a handler over that.
    /// </summary>
    /// <param name="module"></param>
    /// <param name="configure"></param>
    static (ServiceProvider Services, INodeRequestHandler Handler) Build(string module, Action<FetchRequestHandlerOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddNodeEnginePool();
        var provider = services.BuildServiceProvider();

        var options = new FetchRequestHandlerOptions() { Module = TestModules.FromText("app.cjs", module) };
        configure?.Invoke(options);

        return (provider, new FetchRequestHandler(provider.GetRequiredService<NodeEnginePool>(), options));
    }

    [TestMethod]
    public async Task A_module_exports_assignment_is_the_application()
    {
        // Some bundlers assign a default-only module straight to module.exports rather than hanging
        // a default property off it; both interop shapes must serve.
        const string Module =
            "module.exports = { " +
            "fetch(request) { return new Response('direct:' + new URL(request.url).pathname, { status: 200 }); } };";

        var (services, engine) = Build(Module);
        await using var _ = services;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/x");
        using var response = await engine.SendAsync(request);
        Assert.AreEqual("direct:/x", await response.Content.ReadAsStringAsync());
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
    public async Task Asynchronous_startup_memoizes_in_module_scope()
    {
        // There is no init export: the host asks nothing of the application beyond fetch. Asynchronous
        // per-engine startup is memoized in module scope, exactly as a Workers application does for
        // per-isolate setup, and the module cache makes that once per engine.
        const string Module = """
            let startups = 0;
            let ready;
            const start = () => ready ??= (async () => { startups++; await new Promise(r => setTimeout(r, 10)); return 'yes'; })();
            module.exports.default = {
                async fetch(request) {
                    return new Response(await start() + ':' + startups, { status: 200 });
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
    public async Task The_environment_reaches_the_handler()
    {
        // The convention's env argument: the place for what only the host knows. Input only  14 it is
        // rebuilt per request, so an application keeps its own state in module scope instead.
        const string Module = """
            module.exports.default = {
                fetch(request, env) {
                    return new Response(env.ApiBaseUri + '|' + env.Name, { status: 200 });
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

        Assert.AreEqual("http://api.internal:8080/|unit", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task A_function_default_still_carries_its_properties()
    {
        // Functions are objects, so a factory can hang state off the handler it returns — which is
        // where a provider written against such a framework would go looking for its router.
        const string Module = """
            const handler = (request) => new Response('ok', { status: 200 });
            handler.router = [{ path: '/x/:id', id: 'x' }];
            module.exports.default = handler;
            """;

        var services = new ServiceCollection();
        services.AddNodeEnginePool();
        await using var provider = services.BuildServiceProvider();

        var pool = provider.GetRequiredService<NodeEnginePool>();
        var source = TestModules.FromText("app.cjs", Module);

        // A route provider reads the module the same way anything else does — pool, module, done —
        // and the resolution finds the function's properties rather than stopping at the function.
        var id = await pool.RunAsync(source, exports =>
            Task.FromResult((string)NodeModuleExports.Default(exports)["router"][0]["id"]));

        Assert.AreEqual("x", id);
    }

}
