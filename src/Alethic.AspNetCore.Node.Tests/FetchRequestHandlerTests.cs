using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.AspNetCore.Node.Tests;

/// <summary>
/// Exercises the request handler through its interface: HTTP in, HTTP out.
/// </summary>
[TestClass]
public class FetchRequestHandlerTests
{

    /// <summary>
    /// An application whose fetch echoes enough of the request to assert on.
    /// </summary>
    const string EchoModule = """
        module.exports.default = {
            fetch(request) {
                const url = new URL(request.url);
                const headers = {};
                for (const [k, v] of request.headers.entries())
                    headers[k] = v;
                return new Response(
                    JSON.stringify({ url: request.url, path: url.pathname, method: request.method, headers }),
                    { status: 200, headers: { 'content-type': 'application/json', 'x-app': 'yes' } });
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
    /// Registers a pool, builds an application on it, and a handler over that.
    /// </summary>
    /// <param name="module"></param>
    /// <param name="configurePool"></param>
    static (ServiceProvider Services, INodeRequestHandler Handler) Build(string module, Action<NodeEnginePoolOptions>? configurePool = null)
    {
        var services = new ServiceCollection();
        services.AddNodeEnginePool(configurePool);
        var provider = services.BuildServiceProvider();

        return (provider, new FetchRequestHandler(
            provider.GetRequiredService<NodeEnginePool>(),
            new FetchRequestHandlerOptions() { Module = TestModules.FromText("app.cjs", module) }));
    }

    [TestMethod]
    public async Task The_application_is_asked_at_its_configured_address()
    {
        var services = new ServiceCollection();
        services.AddNodeEnginePool();
        await using var provider = services.BuildServiceProvider();

        var handler = new FetchRequestHandler(
            provider.GetRequiredService<NodeEnginePool>(),
            new FetchRequestHandlerOptions()
            {
                Module = TestModules.FromText("app.cjs", EchoModule),
                BaseUri = new Uri("http://somewhere.invalid/app/"),
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/parks/enchanted-rock");
        using var response = await handler.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        // The configured address, with the request's own path inserted under its prefix — and not
        // the caller's authority, which reaches the application only through the headers.
        StringAssert.Contains(text, "\"url\":\"http://somewhere.invalid/app/parks/enchanted-rock\"");
        StringAssert.Contains(text, "\"x-forwarded-host\":\"unit.test\"");
    }

    [TestMethod]
    public async Task Send_renders_a_request()
    {
        var (services, engine) = Build(EchoModule);
        await using var _ = services;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/parks/enchanted-rock");
        request.Headers.Add("x-probe", "value");

        using var response = await engine.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("yes", response.Headers.GetValues("x-app").Single());
        StringAssert.Contains(text, "\"url\":\"http://node.invalid/parks/enchanted-rock\"");
        StringAssert.Contains(text, "\"path\":\"/parks/enchanted-rock\"");
        StringAssert.Contains(text, "\"method\":\"GET\"");
        StringAssert.Contains(text, "\"x-probe\":\"value\"");
    }

    [TestMethod]
    public async Task A_request_body_is_read_as_the_application_asks_for_it()
    {
        const string ReaderModule = """
            module.exports.default = {
                async fetch(request) {
                    const reader = request.body.getReader();
                    let bytes = 0, chunks = 0, sum = 0;

                    while (true) {
                        const { done, value } = await reader.read();
                        if (done)
                            break;

                        chunks++;
                        bytes += value.length;
                        for (const b of value)
                            sum = (sum + b) % 1000003;
                    }

                    return new Response(JSON.stringify({ bytes, chunks, sum }), { status: 200 });
                },
            };
            """;

        var (services, engine) = Build(ReaderModule);
        await using var _ = services;

        var payload = new byte[100 * 1024];
        var expected = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
            expected = (expected + payload[i]) % 1000003;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/upload")
        {
            Content = new ByteArrayContent(payload),
        };

        using var response = await engine.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        // More than one piece, which is the whole point: the application pulled repeatedly rather
        // than being handed the body entire, so what is in memory at once is a chunk and not an
        // upload. Every byte still arrived, in order.
        var chunks = int.Parse(Regex.Match(text, @"""chunks"":(\d+)").Groups[1].Value);
        Assert.IsTrue(chunks > 1, $"the body arrived in {chunks} piece(s), so it was not streamed");

        StringAssert.Contains(text, $"\"bytes\":{payload.Length}");
        StringAssert.Contains(text, $"\"sum\":{expected}");
    }

    [TestMethod]
    public async Task A_request_body_the_application_never_reads_does_not_hang()
    {
        const string IgnoresBodyModule = """
            module.exports.default = {
                fetch(request) {
                    return new Response('ignored', { status: 200 });
                },
            };
            """;

        var (services, engine) = Build(IgnoresBodyModule);
        await using var _ = services;

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/upload")
        {
            Content = new ByteArrayContent(new byte[64 * 1024]),
        };

        // Nothing pulls, so nothing is ever read — the render must not wait on a body the
        // application had no interest in.
        using var response = await engine.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ignored", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task A_buffered_request_body_can_be_read_twice()
    {
        const string ClonesModule = """
            module.exports.default = {
                async fetch(request) {
                    const once = await request.clone().text();
                    const twice = await request.text();
                    return new Response(once + '|' + twice, { status: 200 });
                },
            };
            """;

        var services = new ServiceCollection();
        services.AddNodeEnginePool();
        await using var provider = services.BuildServiceProvider();

        var handler = new FetchRequestHandler(
            provider.GetRequiredService<NodeEnginePool>(),
            new FetchRequestHandlerOptions()
            {
                Module = TestModules.FromText("app.cjs", ClonesModule),
                RequestBody = BodyMode.Buffered,
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/")
        {
            Content = new StringContent("park data"),
        };

        using var response = await handler.SendAsync(request);

        // What a stream cannot do, and the reason the mode exists.
        Assert.AreEqual("park data|park data", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task A_buffered_response_carries_a_length()
    {
        var services = new ServiceCollection();
        services.AddNodeEnginePool();
        await using var provider = services.BuildServiceProvider();

        var handler = new FetchRequestHandler(
            provider.GetRequiredService<NodeEnginePool>(),
            new FetchRequestHandlerOptions()
            {
                Module = TestModules.FromText("app.cjs", EchoModule),
                ResponseBody = BodyMode.Buffered,
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/parks/enchanted-rock");
        using var response = await handler.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        // Nothing is written until the render has finished, so its length is known by then and the
        // response says so rather than being framed as chunked.
        Assert.AreEqual(text.Length, response.Content.Headers.ContentLength);
        StringAssert.Contains(text, "\"path\":\"/parks/enchanted-rock\"");
    }

    [TestMethod]
    [DataRow(BodyMode.Streamed, "partial")]
    [DataRow(BodyMode.Buffered, "")]
    public async Task What_a_late_failure_leaves_behind_depends_on_the_mode(BodyMode mode, string expected)
    {
        const string FailsLateModule = """
            module.exports.default = {
                fetch(request) {
                    let sent = false;

                    // Erred from a later pull, not alongside the enqueue: error() discards whatever is
                    // queued, so a chunk has to have been handed over before the failure to be lost by
                    // it.
                    const body = new ReadableStream({
                        pull(controller) {
                            if (!sent) {
                                sent = true;
                                controller.enqueue(new TextEncoder().encode('partial'));
                                return;
                            }

                            controller.error(new Error('failed after the head'));
                        },
                    });

                    return new Response(body, { status: 200, headers: { 'content-type': 'text/plain' } });
                },
            };
            """;

        var services = new ServiceCollection();
        services.AddNodeEnginePool();
        await using var provider = services.BuildServiceProvider();

        var handler = new FetchRequestHandler(
            provider.GetRequiredService<NodeEnginePool>(),
            new FetchRequestHandlerOptions()
            {
                Module = TestModules.FromText("app.cjs", FailsLateModule),
                ResponseBody = mode,
            });

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/";

        var written = new MemoryStream();
        context.Response.Body = written;

        await Assert.ThrowsAsync<Exception>(() => handler.HandleAsync(context));

        // The render fails after its first chunk either way. Streamed, that chunk is already on the
        // wire and the client is left holding a truncated page under a 200 it cannot distinguish
        // from a whole one. Buffered, nothing has been written, so the failure is still a failure.
        Assert.AreEqual(expected, Encoding.UTF8.GetString(written.ToArray()));
    }

    [TestMethod]
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
        Assert.AreEqual("got:park data", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task The_renderer_enumerates_no_routes()
    {
        var (services, handler) = Build(EchoModule);
        await using var _ = services;

        // A request handler answers requests. The fetch convention describes a handler and says nothing
        // and reading them is a separate object over the same application.
        Assert.IsNotInstanceOfType<INodeRouteProvider>(handler);
    }

    [TestMethod]
    public async Task Module_scope_persists_across_renders_on_an_engine()
    {
        const string CountingModule = """
            let calls = 0;
            module.exports.default = {
                fetch(request) { return new Response(String(++calls), { status: 200 }); },
            };
            """;

        var (services, engine) = Build(CountingModule, o => o.EngineCount = 1);
        await using var _ = services;

        async Task<string> Once()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");
            using var response = await engine.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }

        // require caches by resolved filename, so the module evaluates once per engine and its scope
        // outlives any one render. This is what an application memoizes asynchronous startup in, and
        // it is the reason the host holds nothing per engine of its own.
        Assert.AreEqual("1", await Once());
        Assert.AreEqual("2", await Once());
        Assert.AreEqual("3", await Once());
    }

    [TestMethod]
    public async Task The_execution_context_is_the_third_argument()
    {
        const string ContextModule = """
            module.exports.default = {
                fetch(request, env, ctx) {
                    if (typeof ctx?.waitUntil !== 'function') throw new Error('no waitUntil');
                    if (typeof ctx?.passThroughOnException !== 'function') throw new Error('no passThroughOnException');
                    ctx.waitUntil(Promise.resolve('background work'));
                    return new Response('ok', { status: 200 });
                },
            };
            """;

        var (services, engine) = Build(ContextModule);
        await using var _ = services;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");
        using var response = await engine.SendAsync(request);

        // fetch(request, env, ctx), as the convention specifies. A handler reaching for ctx.waitUntil
        // is the ordinary case for frameworks targeting Workers, and it must not find undefined.
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
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
        await Assert.ThrowsAsync<Exception>(() => send).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Missing_default_export_fails_loudly()
    {
        var (services, engine) = Build("module.exports.notDefault = 1;");
        await using var _ = services;

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://unit.test/");

        var e = await Assert.ThrowsAsync<Exception>(() => engine.SendAsync(request));
        StringAssert.Contains(e.Message, "fetch");
    }

    [TestMethod]
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

    [TestMethod]
    public async Task Prepare_fails_on_an_unreadable_module()
    {
        var services = new ServiceCollection();
        services.AddNodeEnginePool();
        await using var provider = services.BuildServiceProvider();

        var handler = new FetchRequestHandler(
            provider.GetRequiredService<NodeEnginePool>(),
            new FetchRequestHandlerOptions() { Module = NodeModuleSource.FromFile("Z:/does/not/exist.cjs") });

        // A broken module must fail preparation — and with it the deployment — rather than stand up
        // an engine that quietly serves nothing.
        await Assert.ThrowsAsync<Exception>(() => handler.PrepareAsync());
    }

}
