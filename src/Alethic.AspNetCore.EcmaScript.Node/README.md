# Alethic.AspNetCore.EcmaScript.Node

Server-side rendering on a real Node runtime embedded in the .NET process, through
[node-api-dotnet](https://github.com/microsoft/node-api-dotnet). No sidecar process, no HTTP hop.

Two things register into DI: the engine pool, and rendering engines on it.

```csharp
builder.Services.AddNodeEnginePool(o =>
{
    o.EngineCount = 4;                 // must track the CPU limit; see remarks on the option
    o.MaxConcurrencyPerEngine = 4;     // backpressure, not mutual exclusion
});
builder.Services.AddNodeRenderEngine(o =>
{
    o.Module = NodeModuleSource.FromFile("ssr/server.cjs");
    o.Environment["ApiBaseUri"] = "http://api.internal:8080/";
});

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();

await app.MapRenderEngineAsync();      // from Alethic.AspNetCore.EcmaScript

await app.RunAsync();
```

Both registrations are keyable, for applications running several pools or engines; a rendering
engine names its pool with `PoolKey`.

## The application module

A self-contained CommonJS bundle following the module-worker convention — the
`export default { fetch }` shape shared by Cloudflare Workers, Deno, Bun, and the frameworks that
target them. Everything is optional except `fetch`:

```js
export default {
    async init(env) { /* optional: awaited once per engine, before anything else */ },
    fetch(request, env) { /* return a Response; sync or async */ },
    routes() {
        return [
            { pattern: '/parks/:parkRef', renderMode: 'Server' },
            { pattern: '/profile', renderMode: 'Client' },   // never touches the engine
        ];
    },
};
```

- **`fetch(request, env)`** — the Web-standard handler. A bare function as the default export is
  accepted too, which is what `createRequestHandler`-style factories produce. `env` carries the
  values supplied through `NodeRenderEngineOptions.Environment` — what only the host knows.
- **`init(env)`** — awaited once per engine before the first render. It exists because a CommonJS
  bundle cannot top-level-await; a failing init fails the deployment.
- **`routes()`** — the manifest, patterns in URLPattern pathname syntax. Frameworks with entries
  in this shape already (Hono and friends) work verbatim; the rest wrap in a few lines.

## The pool on its own

The pool is a concrete facility, not an abstraction: it is libnode, on purpose, and usable for any
JavaScript work. A one-shot puts you on an engine's thread writing ordinary node-api-dotnet:

```csharp
var pool = provider.GetRequiredService<NodeEnginePool>();

var result = await pool.RunAsync(NodeModuleSource.FromFile("tool.cjs"), async exports =>
    (int)await ((JSPromise)exports.CallMethod("transform", input)).AsTask());
```

For several steps that must share one engine — per-engine module state, or a claim that outlives a
single call — take a lease instead: `await using var lease = await pool.AcquireAsync();` and run
against it. A lease is a capacity claim and an affinity pin, not exclusivity; engines overlap many
concurrent calls regardless.

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
- **Responses stream.** A failure after the first byte can truncate the body but cannot change the
  status.
- **Cancellation aborts the render**, through `AbortSignal` on the request, not merely the wait.
