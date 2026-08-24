# Alethic.AspNetCore.Node

Server-side rendering for ASP.NET Core, on a real Node runtime embedded in the process through
[node-api-dotnet](https://github.com/microsoft/node-api-dotnet). No sidecar process, no HTTP hop.

The repository previously carried a fork of MintPlayer.AspNetCore.SpaServices, the community
continuation of Microsoft's SpaServices.Extensions. That model — JSON-RPC into a Node child
process, per-framework prerenderer builders, routes re-declared in C# — is retired and removed;
the final state of that lineage is preserved on the `wip/spa-restructure-2025-02` branch.

## The three pieces

**Engines run JavaScript.** `NodeEnginePool` holds several, each a libnode runtime on its own
thread. Openly libnode, not an abstraction: registered in DI, pooled for CPU parallelism, and usable
for any JavaScript work — a lease puts you on an engine's thread writing ordinary node-api-dotnet.
There is one package, and it names the runtime it runs on, because that is the only runtime it runs
on.

**Modules are Node's.** A module is loaded with `require` and cached in `require.cache` by resolved
filename, so it evaluates once per engine and keeps its module scope — the identity a module has in
any other Node program. This library caches nothing of its own, and holds no per-engine state at all.

**A request handler answers requests.** `INodeRequestHandler` is prepare and send, and separates
application *protocols*, not runtimes — every implementation runs on the same engines. `FetchRequestHandler`
calls the application's `fetch` handler, which most current SSR frameworks expose or compose with.
The `Request` is built and the `Response` taken apart directly on the engine's thread, where those
types live; nothing is serialized and the module evaluates untouched.

**A route provider says what routes exist.** `INodeRouteProvider` is a separate object because
routing is not part of the fetch convention — that convention describes a handler and says nothing
about routes. Written against a framework, it reads the router the application already dispatches
on, so nothing is declared twice and nothing can drift. Mounting without one serves the application
whole from a fallback endpoint, which is the honest outcome rather than a degradation.

## Shape

One registration — the pool. Everything else is constructed where it is mounted:

```csharp
builder.Services.AddNodeEnginePool(o =>
{
    o.EngineCount = 4;                 // must track the CPU limit; see remarks on the option
    o.MaxConcurrencyPerEngine = 4;     // backpressure, not mutual exclusion
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();                      // explicit, or the fallback outruns static files

// Renders through the application's fetch handler, mounted on a fallback endpoint. No routes: a
// fetch handler describes none.
app.MapNodeFetchHandler(o => o.Module = NodeModuleSource.FromFile("ssr/server.cjs"));

await app.RunAsync();
```

The pool is the only thing worth registering: it owns the threads, and the whole application shares
one. For an endpoint per route, pass a handler and a provider naming the same module — which is all
the sharing they need, since Node loads it once per engine and both see that instance:

```csharp
var pool = app.Services.GetRequiredService<NodeEnginePool>();
var source = NodeModuleSource.FromFile("ssr/server.cjs");

app.MapNode(
    new FetchRequestHandler(pool, new() { Module = source }),
    new MyRouteProvider(pool, source));
```

## The application contract

The application's server module is a self-contained CommonJS bundle exporting a fetch handler - the
shape Cloudflare Workers defines and Deno, Bun, and the framework adapters targeting them follow:

```js
export default {
    fetch(request, env, ctx) { /* return a Response; sync or async */ },
};
```

- **`request`** - a real `Request`, built on the engine's thread.
- **`env`** - what only the host knows, from `FetchRequestHandlerOptions.Environment`. Input only:
  built fresh per request. Cloudflare puts live bindings here; this host has strings to give, which
  the convention permits since it leaves the contents to the host.
- **`ctx`** - `waitUntil(promise)` and `passThroughOnException()`. `waitUntil` matters on Workers
  because the isolate is frozen after the response; a pooled engine is not, so the promise runs
  either way and `waitUntil` only keeps its rejection from going unobserved.

Two deliberate departures: a bare function default export is accepted as the handler, which Workers
does not allow but `createRequestHandler`-style factories produce; and `scheduled`, `queue`, and
`tail` are not called, this being an HTTP handler.

Nothing else is asked. There is no init hook and no route export. Asynchronous per-engine startup is
the application's own, memoized in module scope exactly as a Workers application memoizes
per-isolate setup - module scope being per isolate there and per engine here.

The per-framework cost of that contract:

| Framework | Entry |
|---|---|
| Hono, Elysia, Nitro (worker preset) | `export default app` |
| React Router 7 / Remix | `export default createRequestHandler(build)` — the bare function is accepted |
| Astro | wrap `app.render(request)` in a few lines |
| SvelteKit | `fetch` calls `server.respond(request)`; `server.init({ env })` memoized in module scope |
| Angular | wrap `AngularAppEngine.handle(request)`, mapping its null to a 404 or the shell |

Where a framework does not expose a fetch handler at all, the answer is another `INodeRequestHandler`
rather than glue in the application: it mounts through the same `MapNode` and runs on the
same engines. That is rare. Far more often the handler is fine and only the *routes* are
framework-specific — TanStack Start, say — which needs a route provider and no handler at all.

## Routes

The fetch convention has no notion of routes, so nothing above produces any. An endpoint per route
comes from an `INodeRouteProvider`, written against a framework and reading the router that framework
already has — React Router's build manifest, TanStack Start's route tree, SvelteKit's manifest:

```csharp
public Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken ct = default) =>
    pool.RunAsync(module, exports =>
    {
        // On the engine's thread. Nothing is serialized; only plain .NET data leaves the scope.
        var table = NodeModuleExports.Default(exports)["router"];
        ...
    }, ct);
```

The application is asked for nothing it would not have anyway. Routes are read from where they
already live rather than declared a second time beside them, which is what keeps them from drifting.

`RenderRoute.Pattern` is a URLPattern pathname — the WHATWG standard every framework's route grammar
lowers to — which the host converts to an ASP.NET route template. `RenderMode` is the per-route
policy: `Client` (shell only, the engine never invoked), `Server`, or `Prerender`. A route's `id`
names its endpoint, so `LinkGenerator.GetPathByName` builds URLs from the router itself.

An engine that provides no routes is served entirely from the fallback — the honest outcome for a
protocol that describes none. A provider that *throws* fails the deployment, because an engine that
cannot read the router it was written for is broken, and that must not pass for an application that
simply has no routes.

## The pool on its own

The pool works with no web anywhere in sight — a lease is a claim on one engine, and inside
`RunAsync` you are on its thread writing node-api-dotnet:

```csharp
var pool = provider.GetRequiredService<NodeEnginePool>();

var result = await pool.RunAsync(NodeModuleSource.FromFile("tool.cjs"), async exports =>
    (int)await ((JSPromise)exports.CallMethod("transform", input)).AsTask());
```

## Constraints worth knowing

- **Engine count must be configured, never derived.** Inside a container the processor count
  reports the host's cores, not the quota. Engines exist for CPU parallelism; one engine already
  overlaps many concurrent renders, because everything a module awaits yields to its event loop.
- **CommonJS only.** The embedded runtime registers no dynamic-import callback, so ES modules and
  `import()` do not resolve. Bundle fully static (esbuild `--format=cjs`, code splitting off).
- **Responses stream.** The response is returned when its head is known and the body follows; a
  failure after the first byte can truncate the body but cannot change the status.
- **Cancellation aborts the render**, through `AbortSignal` on the request, not merely the wait.
- Reference the RID-specific `Microsoft.JavaScript.LibNode.<rid>` package. The umbrella package
  depends on every platform at once and lands ~640 MB of native libraries in the output.

## Samples

- `samples/Sample.React` — the web path end to end: React 19 server rendering with suspended data
  resolved into the markup, client hydration over it, and a route provider reading the application's
  own router to drive the endpoint table. Build the client with `npm run build` there, then
  `dotnet run` the server.
- `samples/Sample.Console` — the pool with no web anywhere in sight: a console application takes a
  lease and drives a plain JavaScript module, synchronous calls, promises, and structured results
  alike, through ordinary node-api-dotnet.
