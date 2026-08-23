# Alethic.AspNetCore.EcmaScript

The rendering-engine abstraction for ASP.NET Core: HTTP request in, rendered response out.

`IRenderEngine` is the whole contract. How rendering happens — what runtime, what language,
whether anything is pooled — is entirely an implementation's affair, and nothing of it appears
here:

```csharp
public interface IRenderEngine
{
    Task PrepareAsync(CancellationToken cancellationToken = default);
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RenderRoute>?> GetRoutesAsync(CancellationToken cancellationToken = default);
}
```

Responses stream: they are returned as soon as status and headers are known, and the body follows
as it is produced. Cancellation aborts the rendering itself, not merely the wait for it.

## Mounting an engine

`MapRenderEngineAsync` prepares the engine ahead of traffic, reads the application's route
manifest, and maps an endpoint per route plus a fallback:

```csharp
var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();                      // explicit, or the fallback outruns static files

await app.MapRenderEngineAsync();

await app.RunAsync();
```

- Routes marked `Client` are never mapped — whatever already serves the application shell keeps
  serving them, and the engine is never invoked on their behalf.
- A route's `id` names its endpoint, so `LinkGenerator.GetPathByName` builds URLs from the
  manifest — canonical redirects and sitemaps use the platform facility.
- `ConfigureEndpoint` applies host policy per route: output caching by render mode, authorization
  by path, anything the endpoint builder carries.
- An engine that cannot prepare fails startup, and with it the deployment, rather than quietly
  serving nothing.

## The route manifest

Patterns are URLPattern pathnames — the WHATWG syntax every framework's route grammar lowers to —
so an application declares its routes without knowing anything about its host:

```json
[
    { "pattern": "/parks/:parkRef", "renderMode": "Server",    "id": "park" },
    { "pattern": "/about",          "renderMode": "Prerender", "id": "about" },
    { "pattern": "/profile",        "renderMode": "Client",    "id": "profile" }
]
```

`RenderMode` is the per-route policy: `Client` (shell only, engine never invoked), `Server`
(rendered per request), or `Prerender` (served like `Server`; the signal for caching or a build
step). A pattern beyond what a route template can express is simply served by the fallback,
losing only its per-route policy.

## Implementations

- **Alethic.AspNetCore.EcmaScript.Node** — renders on a Node runtime embedded in the process.
