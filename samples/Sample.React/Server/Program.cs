using Alethic.AspNetCore.EcmaScript.Node;

var builder = WebApplication.CreateBuilder(args);

// The two registrations: the libnode pool, and the rendering engine on it.
builder.Services.AddNodeEnginePool(o =>
{
	o.EngineCount = 2;
	o.MaxConcurrencyPerEngine = 4;
});
builder.Services.AddNodeRenderEngine(o =>
{
	o.Module = NodeModuleSource.FromFile(Path.Combine(builder.Environment.ContentRootPath, "..", "Client", "dist", "server", "app.cjs"));
});

var app = builder.Build();

// The client bundle the server-rendered document references. The explicit UseRouting matters:
// minimal hosting otherwise inserts routing ahead of every middleware, the fallback endpoint gets
// selected before static files ever run, and the bundle comes back as server-rendered HTML.
app.UseStaticFiles();
app.UseRouting();

// Prepares the engine, asks the application for its routes, and maps them.
var routes = app.MapRenderEngine();
app.Logger.LogInformation("Mounted {Count} routes from the application manifest.", routes.Count);

await app.RunAsync();
