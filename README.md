# Alethic.AspNetCore.EcmaScript

Runs JavaScript applications inside a .NET process, on a real Node runtime embedded through
[node-api-dotnet](https://github.com/microsoft/node-api-dotnet). No sidecar process, no HTTP hop:
the application's server bundle is evaluated in-process and driven through the Web-standard fetch
contract, `(Request) => Promise<Response>`, which every current SSR framework — React, Angular,
SvelteKit, React Router — exposes or composes with.

The repository previously carried a fork of MintPlayer.AspNetCore.SpaServices, the community
continuation of Microsoft's SpaServices.Extensions. That model — JSON-RPC into a Node child
process, per-framework prerenderer builders, routes re-declared in C# — is retired and removed;
the final state of that lineage is preserved on the `wip/spa-restructure-2025-02` branch.

## Layout

| Project | Contents |
|---|---|
| `Alethic.EcmaScript.Hosting` | Engines, pools, modules, options. Knows nothing of HTTP or the web: its two primitives are a JSON invocation and a general streaming invocation (structured head + byte stream + cancellation). |
| `Alethic.EcmaScript.Hosting.Node` | The embedded-Node backend. Implements only the general contract. |
| `Alethic.EcmaScript.Hosting.Http` | The Web-standard fetch contract, built on the streaming primitive. The `Request`/`Response` handling lives in a JS glue function appended to the module, since those are the runtime's types. `System.Net.Http` only. |
| `Alethic.AspNetCore.EcmaScript` | Endpoint mapping: `MapJavaScriptApplicationAsync` warms the pool, reads the application's own route manifest, and maps an endpoint per route plus a fallback. |

## Shape

```csharp
builder.Services.AddJavaScriptEnginePool(o =>
{
    o.EngineCount = 4;                 // must track the CPU limit; see remarks on the option
    o.MaxConcurrencyPerEngine = 4;     // backpressure, not mutual exclusion
})
.UseEmbeddedNode();

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();                      // explicit, or the fallback outruns static files

await app.MapJavaScriptApplicationAsync(new JavaScriptApplicationOptions()
{
    Module = JavaScriptModuleSource.FromFile("ssr/server.cjs"),
});

await app.RunAsync();
```

The pool is equally usable with no web anywhere in sight — run any JS, stream any bytes:

```csharp
var pool = provider.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
var module = pool.GetModule(JavaScriptModuleSource.FromFile("tool.cjs"));

var result = await module.InvokeAsync<MyResult>("transform", [input], ct);
await using var stream = await module.InvokeStreamAsync("produce", [args], payload, ct);
```

The module is a self-contained CommonJS bundle whose default export carries `fetch(request)` and
whatever other exports the host wants to call:

```js
export default {
    fetch(request) { /* return a Response; sync or async */ },
    routes() { /* optional: route manifest for the host */ },
};
```

## Constraints worth knowing

- **Engines are threads; modules may share one.** A single engine overlaps many concurrent calls,
  because everything a module awaits yields to its event loop. Engine count exists for CPU
  parallelism and must be configured, never derived: inside a container the processor count lies.
- **CommonJS only.** The embedded runtime registers no dynamic-import callback, so ES modules and
  `import()` do not resolve. Bundle fully static (esbuild `--format=cjs`, code splitting off).
- **Responses stream.** The response is returned when its head is known and the body follows; a
  failure after the first byte can truncate the body but cannot change the status.
- **Cancellation aborts the render**, through `AbortSignal` on the request, not merely the wait.
- Reference the RID-specific `Microsoft.JavaScript.LibNode.<rid>` package. The umbrella package
  depends on every platform at once and lands ~640 MB of native libraries in the output.

A complete React 19 sample — server rendering with suspended data resolved into the markup, client
hydration over it, and a route manifest driving the endpoint table — lives under
`samples/Sample.React`. Build the client with `npm run build` there, then `dotnet run` the server.
