using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.JavaScript.NodeApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Alethic.AspNetCore.EcmaScript.Node.Tests;

/// <summary>
/// Exercises the pool as what it is: a concrete facility for running JavaScript, with ordinary
/// node-api-dotnet written inside a lease. Nothing here is web-shaped.
/// </summary>
[TestClass]
public class NodeEnginePoolTests
{

	/// <summary>
	/// Builds a provider with a pool registered.
	/// </summary>
	/// <param name="configure"></param>
	static ServiceProvider BuildServices(Action<NodeEnginePoolOptions>? configure = null)
	{
		var services = new ServiceCollection();
		services.AddNodeEnginePool(configure);
		return services.BuildServiceProvider();
	}

	[TestMethod]
	public async Task Lease_runs_javascript_on_the_engine_thread()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<NodeEnginePool>();

		await using var lease = await pool.AcquireAsync();
		var result = await lease.RunAsync(() => Task.FromResult((int)JSValue.RunScript("6 * 7")));

		Assert.AreEqual(42, result);
	}

	[TestMethod]
	public async Task Modules_evaluate_once_per_engine_and_keep_state()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<NodeEnginePool>();
		var module = NodeModuleSource.FromText("counter.cjs", "let n = 0; module.exports.next = () => ++n;");

		async Task<int> NextAsync()
		{
			await using var lease = await pool.AcquireAsync();
			return await lease.RunAsync(module, exports => Task.FromResult((int)exports.CallMethod("next")));
		}

		// One engine, so successive leases see the same evaluated module: the counter advances
		// rather than resetting, which is the evaluated-once contract of a module source.
		Assert.AreEqual(1, await NextAsync());
		Assert.AreEqual(2, await NextAsync());
	}

	[TestMethod]
	public async Task Promises_and_timers_work_inside_a_lease()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<NodeEnginePool>();
		var module = NodeModuleSource.FromText("later.cjs",
			"module.exports.later = (v) => new Promise(r => setTimeout(() => r(v * 2), 10));");

		await using var lease = await pool.AcquireAsync();
		var result = await lease.RunAsync(module, async exports =>
		{
			var pending = (JSPromise)exports.CallMethod("later", 21);
			return (int)await pending.AsTask();
		});

		Assert.AreEqual(42, result);
	}

	[TestMethod]
	public async Task Concurrent_leases_overlap_on_one_engine()
	{
		await using var services = BuildServices(o => o.MaxConcurrencyPerEngine = 8);
		var pool = services.GetRequiredService<NodeEnginePool>();
		var module = NodeModuleSource.FromText("slow.cjs", """
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
			""");

		async Task<int> One()
		{
			await using var lease = await pool.AcquireAsync();
			return await lease.RunAsync(module, async exports =>
				(int)await ((JSPromise)exports.CallMethod("slow", 100)).AsTask());
		}

		var peaks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => One()));

		Assert.IsTrue(peaks.Max() > 1, "leases never overlapped inside the module");
	}

	[TestMethod]
	public async Task Capacity_is_bounded_and_times_out()
	{
		await using var services = BuildServices(o =>
		{
			o.MaxConcurrencyPerEngine = 1;
			o.AcquireTimeout = TimeSpan.FromMilliseconds(200);
		});
		var pool = services.GetRequiredService<NodeEnginePool>();

		// The single slot is held and never released, so the next acquisition must give up rather
		// than wait forever.
		await using var held = await pool.AcquireAsync();
		await Assert.ThrowsExactlyAsync<TimeoutException>(() => pool.AcquireAsync());
	}

	[TestMethod]
	public async Task One_shot_runs_without_a_lease()
	{
		await using var services = BuildServices();
		var pool = services.GetRequiredService<NodeEnginePool>();
		var module = NodeModuleSource.FromText("add.cjs", "module.exports.add = (a, b) => a + b;");

		// The one-shot acquires and releases internally, so a single call carries no checkout
		// ceremony — and the capacity comes back, which the second call proves.
		Assert.AreEqual(5, await pool.RunAsync(module, exports => Task.FromResult((int)exports.CallMethod("add", 2, 3))));
		Assert.AreEqual(42, await pool.RunAsync(() => Task.FromResult((int)JSValue.RunScript("6 * 7"))));
	}

	[TestMethod]
	public async Task Prepare_stands_engines_up_and_warms_them()
	{
		await using var services = BuildServices(o => o.EngineCount = 2);
		var pool = services.GetRequiredService<NodeEnginePool>();
		var module = NodeModuleSource.FromText("warm.cjs", "module.exports.ok = () => true;");

		var warmed = 0;
		await pool.PrepareAsync(async lease =>
		{
			await lease.ImportAsync(module);
			Interlocked.Increment(ref warmed);
		});

		Assert.AreEqual(2, warmed);
	}

}
