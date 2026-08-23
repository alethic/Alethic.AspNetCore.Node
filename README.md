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
| `Alethic.EcmaScript.Hosting` | Engines, pools, modules, options. No ASP.NET dependency; usable from a console tool. |
| `Alethic.EcmaScript.Hosting.Node` | The embedded-Node backend. |

An ASP.NET Core integration layer (endpoint mapping, route manifests, warmup) sits above these and
is under construction.

## Shape

```csharp
services.AddJavaScriptEnginePool(o =>
{
    o.EngineCount = 4;                 // must track the CPU limit; see remarks on the option
    o.MaxConcurrencyPerEngine = 4;     // backpressure, not mutual exclusion
})
.UseEmbeddedNode();

var pool = provider.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");
var app = pool.GetApplication(JavaScriptModuleSource.FromFile("ssr/server.cjs"));

using var response = await app.SendAsync(request, cancellationToken);   // streams
var routes = await app.InvokeAsync<List<RouteEntry>>("routes", [], cancellationToken);
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
