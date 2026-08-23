using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Alethic.EcmaScript.Hosting;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Alethic.EcmaScript.Hosting.Node.Tests;

/// <summary>
/// Exercises the object model against the embedded Node backend: values, properties, calls,
/// promises, byte data, and the affinity rules.
/// </summary>
[Collection("Node")]
public class NodeObjectModelTests
{

	/// <summary>
	/// A module exercising every value shape the model carries.
	/// </summary>
	const string Module = """
		let count = 0;
		module.exports.add = (a, b) => a + b;
		module.exports.greet = (name) => 'hello ' + name;
		module.exports.flag = (b) => !b;
		module.exports.nothing = () => undefined;
		module.exports.later = (v) => new Promise(r => setTimeout(() => r(v * 2), 10));
		module.exports.count = () => ++count;
		module.exports.state = { nested: { value: 42 } };
		module.exports.bytes = (n) => { const a = new Uint8Array(n); for (let i = 0; i < n; i++) a[i] = i; return a; };
		module.exports.sum = (arr) => arr.reduce((t, x) => t + x, 0);
		module.exports.stream = (chunks) => new ReadableStream({
			start(controller) {
				const encoder = new TextEncoder();
				for (let i = 0; i < chunks; i++)
					controller.enqueue(encoder.encode('chunk' + i + ';'));
				controller.close();
			},
		});
		""";

	/// <summary>
	/// Builds a provider with one default pool on the embedded Node backend.
	/// </summary>
	/// <param name="configure"></param>
	static ServiceProvider BuildServices(Action<JavaScriptEnginePoolOptions>? configure = null)
	{
		var services = new ServiceCollection();
		services.AddJavaScriptEnginePool(configure).UseEmbeddedNode();
		return services.BuildServiceProvider();
	}

	/// <summary>
	/// Opens a session over the test module.
	/// </summary>
	/// <param name="services"></param>
	static Task<IJavaScriptSession> OpenAsync(IServiceProvider services) =>
		services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default")
			.AcquireAsync(JavaScriptModuleSource.FromText("model.cjs", Module));

	[Fact]
	public async Task Primitives_round_trip()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);
		var module = session.Module;

		Assert.Equal(5d, (await module.InvokeAsync("add", [2, 3])).AsNumber());
		Assert.Equal("hello sweep", (await module.InvokeAsync("greet", ["sweep"])).AsString());
		Assert.False((await module.InvokeAsync("flag", [true])).AsBoolean());
		Assert.Equal(JavaScriptValueKind.Undefined, (await module.InvokeAsync("nothing", [])).Kind);
	}

	[Fact]
	public async Task Objects_are_handles_with_readable_properties()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);

		await using var state = (await session.Module.GetAsync("state")).AsObject();
		await using var nested = (await state.GetAsync("nested")).AsObject();

		Assert.Equal(42d, (await nested.GetAsync("value")).AsNumber());

		await nested.SetAsync("value", 43);
		Assert.Equal(43d, (await nested.GetAsync("value")).AsNumber());
	}

	[Fact]
	public async Task Promises_are_awaited_explicitly()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);

		await using var pending = (await session.Module.InvokeAsync("later", [21])).AsObject();
		var settled = await pending.AwaitAsync();

		Assert.Equal(42d, settled.AsNumber());
	}

	[Fact]
	public async Task Awaiting_a_non_promise_settles_to_itself()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);

		await using var state = (await session.Module.GetAsync("state")).AsObject();
		var settled = await state.AwaitAsync();

		await using var again = settled.AsObject();
		await using var nested = (await again.GetAsync("nested")).AsObject();
		Assert.Equal(JavaScriptValueKind.Number, (await nested.GetAsync("value")).Kind);
	}

	[Fact]
	public async Task Typed_arrays_copy_out_as_bytes()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);

		await using var array = (await session.Module.InvokeAsync("bytes", [5])).AsObject();
		var bytes = await array.ToByteArrayAsync();

		Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, bytes);
	}

	[Fact]
	public async Task Byte_arrays_pass_in_as_typed_arrays()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);

		var payload = await session.Engine.CreateByteArrayAsync(new byte[] { 1, 2, 3, 4, 5 });
		await using var handle = payload.AsObject();

		Assert.Equal(15d, (await session.Module.InvokeAsync("sum", [payload])).AsNumber());
	}

	[Fact]
	public async Task Streams_are_just_objects()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);

		// No streaming API anywhere: getReader() and read() are ordinary calls, a chunk is an
		// ordinary result object, and its bytes copy out like any typed array.
		await using var stream = (await session.Module.InvokeAsync("stream", [3])).AsObject();
		await using var reader = (await stream.InvokeAsync("getReader", [])).AsObject();

		var text = new StringBuilder();
		while (true)
		{
			await using var pending = (await reader.InvokeAsync("read", [])).AsObject();
			var settled = await pending.AwaitAsync();
			await using var result = settled.AsObject();

			if ((await result.GetAsync("done")).AsBoolean())
				break;

			await using var chunk = (await result.GetAsync("value")).AsObject();
			text.Append(Encoding.UTF8.GetString(await chunk.ToByteArrayAsync()));
		}

		Assert.Equal("chunk0;chunk1;chunk2;", text.ToString());
	}

	[Fact]
	public async Task Evaluate_reaches_the_engine_directly()
	{
		await using var services = BuildServices();
		await using var session = await OpenAsync(services);

		Assert.Equal(42d, (await session.Engine.EvaluateAsync("6 * 7")).AsNumber());

		await using var controller = (await session.Engine.EvaluateAsync("new AbortController()")).AsObject();
		var signal = await controller.GetAsync("signal");
		Assert.Equal(JavaScriptValueKind.Object, signal.Kind);
		await signal.AsObject().DisposeAsync();
	}

	[Fact]
	public async Task Module_state_persists_within_an_engine()
	{
		await using var services = BuildServices();

		await using (var session = await OpenAsync(services))
			Assert.Equal(1d, (await session.Module.InvokeAsync("count", [])).AsNumber());

		// A new session on the same engine sees the same evaluated module, which is the
		// evaluated-once contract of a module source.
		await using (var session = await OpenAsync(services))
			Assert.Equal(2d, (await session.Module.InvokeAsync("count", [])).AsNumber());
	}

	[Fact]
	public async Task Handles_refuse_to_cross_engines()
	{
		await using var servicesA = BuildServices();
		await using var servicesB = BuildServices();

		await using var sessionA = await OpenAsync(servicesA);
		await using var sessionB = await OpenAsync(servicesB);

		await using var foreign = (await sessionA.Module.GetAsync("state")).AsObject();

		// An engine must reject a handle it did not mint rather than dereference another world's
		// memory; the failure is immediate and named, not a corruption later.
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => sessionB.Module.InvokeAsync("sum", [JavaScriptValue.From(foreign)]));
	}

	[Fact]
	public async Task Concurrent_sessions_overlap_on_one_engine()
	{
		await using var services = BuildServices(o => o.MaxConcurrencyPerEngine = 8);
		var pool = services.GetRequiredService<IJavaScriptEnginePoolProvider>().Get("Default");

		const string Slow = """
			let inFlight = 0, peak = 0;
			module.exports.slow = async (ms) => {
				inFlight++;
				peak = Math.max(peak, inFlight);
				try {
					await new Promise(r => setTimeout(r, ms));
					return peak;
				}
				finally {
					inFlight--;
				}
			};
			""";
		var source = JavaScriptModuleSource.FromText("slow.cjs", Slow);

		async Task<double> One()
		{
			await using var session = await pool.AcquireAsync(source);
			await using var pending = (await session.Module.InvokeAsync("slow", [100])).AsObject();
			return (await pending.AwaitAsync()).AsNumber();
		}

		var peaks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => One()));

		Assert.True(peaks.Max() > 1, "sessions never overlapped inside the module");
	}

}

/// <summary>
/// Serializes every test that touches the embedded runtime.
/// </summary>
[CollectionDefinition("Node", DisableParallelization = true)]
public class NodeCollection
{

}
