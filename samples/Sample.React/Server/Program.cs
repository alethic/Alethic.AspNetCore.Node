using Alethic.AspNetCore.Node;

using Sample.React.Server;

var builder = WebApplication.CreateBuilder(args);

// The one registration: the libnode pool. Everything else is constructed where it is mounted.
builder.Services.AddNodeEnginePool(o =>
{
	o.EngineCount = 2;
	o.MaxConcurrencyPerEngine = 4;
});

var app = builder.Build();

// The client bundle the server-rendered document references. The explicit UseRouting matters:
// minimal hosting otherwise inserts routing ahead of every middleware, the fallback endpoint gets
// selected before static files ever run, and the bundle comes back as server-rendered HTML.
app.UseStaticFiles();
app.UseRouting();

// Two objects over the same pool and the same module, because they vary independently: the
// application exposes a fetch handler, so the stock request handler serves it with nothing written,
// only reading its routes is specific to this application. Naming the same module is all the
// sharing they need — Node loads it once per engine and both see that one instance.
var pool = app.Services.GetRequiredService<NodeEnginePool>();
var source = NodeModuleSource.FromFile(Path.Combine(app.Environment.ContentRootPath, "..", "Client", "dist", "server", "app.cjs"));

var handler = new FetchRequestHandler(pool, new FetchRequestHandlerOptions() { Module = source });

// Prepares the handler, reads the application's routes out of its router, and maps them.
// ConfigureEndpoint is the per-route hook: host policy that varies by route goes here, and it is
// also where the host gets to see what was mounted.
var mounted = new List<RenderRoute>();

app.MapNode(handler, new SampleRouteProvider(pool, source), new MapNodeOptions()
{
	ConfigureEndpoint = (route, _) =>
	{
		if (route is not null)
			mounted.Add(route);
	},
});

app.Logger.LogInformation("Mounted {Count} routes read from the application's router: {Ids}.",
	mounted.Count, string.Join(", ", mounted.Select(r => r.Id)));

await app.RunAsync();
