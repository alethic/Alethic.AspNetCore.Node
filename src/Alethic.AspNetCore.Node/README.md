# Alethic.AspNetCore.Node

Server-side rendering for ASP.NET Core on a real Node runtime embedded in the .NET process, through
[node-api-dotnet](https://github.com/microsoft/node-api-dotnet). No sidecar process, no HTTP hop.

It is libnode, and the package says so. There is no runtime abstraction here and no pretence that
another one could be dropped in underneath.

## Three things

**Engines** run JavaScript. A `NodeEnginePool` holds several, each a libnode runtime on its own
thread. This is the only piece registered in DI: it owns the threads, and the whole application
shares one.

**Modules** are Node's. A module is loaded with `require` and cached in `require.cache` by resolved
filename, so it evaluates once per engine and keeps its module scope — the same identity a module
has in any other Node program. This library caches nothing of its own.

**Request handlers and route providers** work over a pool and a module. A request handler answers
HTTP requests; a route provider says what routes the application serves. Separate, because they vary
independently: most applications need no handler written at all, and only their routes are
framework-specific.

```csharp
builder.Services.AddNodeEnginePool(o =>
{
    o.EngineCount = 4;                 // must track the CPU limit; see remarks on the option
    o.MaxConcurrencyPerEngine = 4;     // backpressure, not mutual exclusion
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();                      // explicit, or the fallback outruns static files

// Renders through the application's fetch handler, mounted on a fallback endpoint.
app.MapNodeFetchHandler(o =>
{
    o.Module = NodeModuleSource.FromFile("ssr/server.cjs");
    o.Environment["ApiBaseUri"] = "http://api.internal:8080/";
});

await app.RunAsync();
```

A fetch handler describes no routes, so that mounts one fallback endpoint — see [Routes](#routes)
for an endpoint per route.

## The application contract

`export default { fetch }`, and nothing else:

```js
export default {
    fetch(request, env, ctx) { /* return a Response; sync or async */ },
};
```

This is the handler shape Cloudflare Workers defines and Deno, Bun, and the framework adapters
targeting them follow. Called with all three arguments:

- **`request`** — a real `Request`, built on the engine's thread.
- **`env`** — what only the host knows, from `FetchRequestHandlerOptions.Environment`. Input only:
  the object is built fresh per request. Cloudflare puts live bindings here; this host has strings
  to give, which the convention permits since it leaves the contents to the host.
- **`ctx`** — `waitUntil(promise)` and `passThroughOnException()`. `waitUntil` matters on Workers
  because the isolate is frozen after the response; a pooled engine is not, so the promise runs
  either way and `waitUntil` only keeps its rejection from going unobserved.
  `passThroughOnException` is a no-op — there is no origin behind this to pass through to.

Two deliberate departures, both stated rather than papered over:

- **A bare function default export is accepted** as the handler, which is what
  `createRequestHandler`-style factories produce. Workers does not accept this.
- **`scheduled`, `queue`, and `tail` are not called.** This is an HTTP handler.

Nothing else is asked of an application. There is no init hook and no route export. Asynchronous
per-engine startup is the application's own, memoized in module scope exactly as a Workers
application memoizes per-isolate setup:

```js
let ready;
const start = () => ready ??= connect(env.ApiBaseUri);
```

Module scope is per isolate there and per engine here, and `require.cache` is what makes that true.

| Framework | Entry |
|---|---|
| Hono, Elysia, Nitro (worker preset) | `export default app` |
| React Router 7 / Remix | `export default createRequestHandler(build)` — the bare function is accepted |
| Astro | wrap `app.render(request)` in a few lines |
| SvelteKit | `fetch` calls `server.respond(request)`; `server.init({ env })` memoized in module scope |
| Angular | wrap `AngularAppEngine.handle(request)`, mapping its null to a 404 or the shell |

## The request handler

`INodeRequestHandler` answers requests. That is the whole of it:

```csharp
public interface INodeRequestHandler
{
    Task PrepareAsync(CancellationToken cancellationToken = default);
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
}
```

Every implementation runs on the same pooled libnode engines — this is not there to abstract the
runtime. It is there because applications do not all speak the same protocol. `FetchRequestHandler`
is the stock one; a framework whose server protocol does not lower to a fetch handler gets its own.
That is rare, and needing routes is not.

`PrepareAsync` loads the module on every engine so that evaluation — which occupies an engine's
event loop — lands at startup rather than under the first request. A module that cannot be loaded
fails there, and with it the deployment.

## Routes

`INodeRouteProvider` is a separate object, passed alongside a handler:

```csharp
public interface INodeRouteProvider
{
    Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken cancellationToken = default);
}
```

Separate because routing is not part of the fetch convention — that convention describes a handler
and says nothing whatever about routes. Mounting without a provider is not a degradation; it is the
honest outcome, and the application is served whole from the fallback.

Write one by reading the router the framework already has:

```csharp
sealed class MyRouteProvider : INodeRouteProvider
{
    readonly NodeEnginePool pool;
    readonly NodeModuleSource module;

    public MyRouteProvider(NodeEnginePool pool, NodeModuleSource module) =>
        (this.pool, this.module) = (pool, module);

    public Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken ct = default) =>
        pool.RunAsync(module, exports =>
        {
            // On the engine's thread. Nothing is serialized; only plain .NET data leaves the scope.
            var table = NodeModuleExports.Default(exports)["router"];
            ...
        }, ct);
}
```

Note what it does *not* do: nothing about rendering, and nothing shared with the handler in .NET.
Naming the same module on the same pool is the whole of the sharing — Node loads it once per engine,
and both see that instance.

```csharp
var pool = app.Services.GetRequiredService<NodeEnginePool>();
var source = NodeModuleSource.FromFile("ssr/server.cjs");

app.MapNode(
    new FetchRequestHandler(pool, new() { Module = source }),
    new MyRouteProvider(pool, source));
```

Routes come from the framework's own router rather than a declaration maintained beside it, so
nothing is stated twice and nothing can drift. `RenderRoute.Pattern` is a URLPattern pathname — the
WHATWG standard every framework's route grammar lowers to — which the host converts to an ASP.NET
route template; a pattern beyond what a template can express falls to the fallback, losing only its
per-route policy. `RenderMode` is that policy: `Client` (shell only, never rendered), `Server`, or
`Prerender`.

A provider that *throws* fails the deployment. A provider that cannot read the router it was written
for is broken, and that must not pass for an application that simply has no routes.

`MapNode` then:

- Skips routes marked `Client` — whatever already serves the application shell keeps serving them.
- Names each endpoint by the route's `id`, so `LinkGenerator.GetPathByName` builds URLs from the
  router: canonical redirects and sitemaps use the platform facility.
- Calls `ConfigureEndpoint` per route for host policy — output caching by render mode, authorization
  by path, anything the endpoint builder carries.
- Returns an `IEndpointConventionBuilder` over everything it mounted, fallback included, so policy
  for the whole application is one statement: `app.MapNode(handler, routes).RequireAuthorization()`.

Each mapped endpoint carries its `RenderRoute` as metadata, so `EndpointDataSource` enumerates what
was mounted — the mapping method does not hand back a list of its own.

## The pool on its own

The pool is a concrete facility: libnode, on purpose, and usable for any JavaScript work, web or
otherwise. A one-shot puts you on an engine's thread writing ordinary node-api-dotnet:

```csharp
var pool = provider.GetRequiredService<NodeEnginePool>();

var result = await pool.RunAsync(NodeModuleSource.FromFile("tool.cjs"), async exports =>
    (int)await ((JSPromise)exports.CallMethod("transform", input)).AsTask());
```

For several steps that must share one engine — or a claim that outlives a single call — take a lease
instead: `await using var lease = await pool.AcquireAsync();` and run against it. A lease is a
capacity claim and an affinity pin, not exclusivity; engines overlap many concurrent calls anyway.

## Constraints worth knowing

- **Reference the RID-specific `Microsoft.JavaScript.LibNode.<rid>` package** for each runtime you
  deploy to. The umbrella package depends on every platform at once and lands ~640 MB of native
  libraries in the output.
- **Engine count must be configured, never derived.** Inside a container the processor count
  reports the host's cores, not the quota. One engine already overlaps many concurrent renders,
  because everything a module awaits yields to its event loop; engines exist for CPU parallelism.
- **CommonJS only.** The embedded runtime registers no dynamic-import callback, so ES modules and
  `import()` do not resolve. Bundle fully static — for esbuild, `--format=cjs` with code splitting
  off.
- **Module scope is shared across concurrent renders on an engine**, since the module is loaded once
  and reused. Per-request state belongs in the request, not at module scope.
- **A rebuilt bundle is not picked up.** `require.cache` holds a module for the runtime's life, so
  rebuilding while the server runs changes nothing until restart.
- **Responses stream.** A failure after the first byte can truncate the body but cannot change the
  status.
- **Cancellation aborts the render**, through `AbortSignal` on the request, not merely the wait.
