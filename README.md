# Alethic.AspNetCore.EcmaScript

Server-side rendering for ASP.NET Core, on a real Node runtime embedded in the process through
[node-api-dotnet](https://github.com/microsoft/node-api-dotnet). No sidecar process, no HTTP hop.

The repository previously carried a fork of MintPlayer.AspNetCore.SpaServices, the community
continuation of Microsoft's SpaServices.Extensions. That model — JSON-RPC into a Node child
process, per-framework prerenderer builders, routes re-declared in C# — is retired and removed;
the final state of that lineage is preserved on the `wip/spa-restructure-2025-02` branch.

## The three pieces

**The abstraction is HTTP request/response.** `IRenderEngine` is the whole of it: send a request,
get a streamed response, ask for the route manifest. Nothing about JavaScript, engines, modules, or
pools appears in it.

**A rendering engine implements it.** `NodeRenderEngine` speaks the Web-standard fetch contract —
`(Request) => Promise<Response>`, which every current SSR framework exposes or composes with — to
the application's server bundle. The `Request` is built and the `Response` taken apart directly on
the engine's thread, where those types live; nothing is serialized and the module evaluates
untouched.

**The libnode pool is a concrete facility underneath.** `NodeEnginePool` is openly libnode, not an
abstraction: registered in DI, pooled for CPU parallelism, and usable for any JavaScript work — a
lease puts you on an engine's thread where you write ordinary node-api-dotnet.

| Package | Contents |
|---|---|
| `Alethic.AspNetCore.EcmaScript` | `IRenderEngine`, the route manifest types, and `MapRenderEngineAsync`. |
| `Alethic.AspNetCore.EcmaScript.Node` | `NodeEnginePool` and `NodeRenderEngine`. |

## Shape

Two registrations — the pool, and the rendering engine on it:

```csharp
builder.Services.AddNodeEnginePool(o =>
{
    o.EngineCount = 4;                 // must track the CPU limit; see remarks on the option
    o.MaxConcurrencyPerEngine = 4;     // backpressure, not mutual exclusion
});
builder.Services.AddNodeRenderEngine(o =>
{
    o.Module = NodeModuleSource.FromFile("ssr/server.cjs");
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();                      // explicit, or the fallback outruns static files

// Prepares the engine, reads the application's own route manifest, maps an endpoint per route.
await app.MapRenderEngineAsync();

await app.RunAsync();
```

The application's server module is a self-contained CommonJS bundle whose default export carries
`fetch(request)` and, optionally, a route manifest in ASP.NET template syntax — emitted by the
application itself, the one place that knows its framework's routing:

```js
export default {
    fetch(request) { /* return a Response; sync or async */ },
    routes() {
        return [
            { pattern: '/parks/{parkRef}', renderMode: 'Server' },
            { pattern: '/profile', renderMode: 'Client' },   // never touches the engine
        ];
    },
};
```

The pool works with no web anywhere in sight — a lease is a claim on one engine, and inside
`RunAsync` you are on its thread writing node-api-dotnet:

```csharp
var pool = provider.GetRequiredService<NodeEnginePool>();
await using var lease = await pool.AcquireAsync();

var result = await lease.RunAsync(NodeModuleSource.FromFile("tool.cjs"), async exports =>
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
  resolved into the markup, client hydration over it, and a route manifest driving the endpoint
  table. Build the client with `npm run build` there, then `dotnet run` the server.
- `samples/Sample.Console` — the pool with no web anywhere in sight: a console application takes a
  lease and drives a plain JavaScript module, synchronous calls, promises, and structured results
  alike, through ordinary node-api-dotnet.
