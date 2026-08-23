using Alethic.AspNetCore.EcmaScript;
using Alethic.EcmaScript.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddJavaScriptEnginePool(o =>
{
	o.EngineCount = 2;
	o.MaxConcurrencyPerEngine = 4;
})
.UseEmbeddedNode();

var app = builder.Build();

// The client bundle the server-rendered document references. The explicit UseRouting matters:
// minimal hosting otherwise inserts routing ahead of every middleware, the fallback endpoint gets
// selected before static files ever run, and the bundle comes back as server-rendered HTML.
app.UseStaticFiles();
app.UseRouting();

// Warms the pool, asks the application for its routes, and maps them. Prerender routes get a tag a
// real deployment would hang an output-cache policy on; here it just shows the seam.
var routes = await app.MapJavaScriptApplicationAsync(new JavaScriptApplicationOptions()
{
	Module = JavaScriptModuleSource.FromFile(Path.Combine(app.Environment.ContentRootPath, "..", "Client", "dist", "server", "app.cjs")),
	ConfigureEndpoint = (route, endpoint) =>
	{
		if (route?.RenderMode == RenderMode.Prerender)
			endpoint.WithDisplayName($"prerenderable:{route.Id}");
	},
});

app.Logger.LogInformation("Mounted {Count} routes from the application manifest.", routes.Count);

await app.RunAsync();
