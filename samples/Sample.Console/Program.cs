using Alethic.AspNetCore.EcmaScript.Node;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JavaScript.NodeApi;

// The pool with no web anywhere in sight: one registration, one lease, ordinary node-api-dotnet
// on the engine's thread. This is the same pool a rendering engine would run on — rendering is
// just one more consumer of it.
var services = new ServiceCollection();
services.AddLogging(logging => logging.AddConsole());
services.AddNodeEnginePool(options => options.EngineCount = 1);

await using var provider = services.BuildServiceProvider();

var pool = provider.GetRequiredService<NodeEnginePool>();
var module = NodeModuleSource.FromFile(Path.Combine(AppContext.BaseDirectory, "tools.cjs"));

await using var lease = await pool.AcquireAsync();

// Synchronous export: call it, take the primitive result out.
var slug = await lease.RunAsync(module, exports =>
	Task.FromResult((string)exports.CallMethod("slugify", "Enchanted Rock State Natural Area")));

// Asynchronous export: the promise is awaited on the engine's thread, where it lives.
var digest = await lease.RunAsync(module, async exports =>
	(string)await ((JSPromise)exports.CallMethod("digest", "enchanted rock")).AsTask());

// Structured result: read the properties off while still on the engine's thread, return plain .NET.
var (characters, words) = await lease.RunAsync(module, exports =>
{
	var stats = exports.CallMethod("stats", "The quick brown fox jumps over the lazy dog");
	return Task.FromResult(((int)stats["characters"], (int)stats["words"]));
});

Console.WriteLine($"slugify : {slug}");
Console.WriteLine($"digest  : {digest}");
Console.WriteLine($"stats   : {characters} characters, {words} words");
