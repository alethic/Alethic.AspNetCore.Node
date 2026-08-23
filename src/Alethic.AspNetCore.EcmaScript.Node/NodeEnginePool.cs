using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alethic.AspNetCore.EcmaScript.Node;

/// <summary>
/// A pool of embedded Node engines.
/// </summary>
/// <remarks>
/// This is a concrete facility, not an abstraction: it is libnode, on purpose, and code holding a
/// lease writes ordinary node-api-dotnet against the engine's runtime. Engines exist for
/// parallelism — JavaScript runs on one thread each, so throughput past a single core needs more of
/// them — and are created as demand requires, up to the configured size. A lease claims capacity on
/// one engine rather than the engine itself; many leases share an engine concurrently, because
/// everything awaited inside it yields to its event loop.
/// </remarks>
public sealed class NodeEnginePool : IAsyncDisposable
{

	readonly NodeEnginePoolOptions options;
	readonly ILoggerFactory loggerFactory;

	readonly List<NodeEngine> engines = [];
	readonly SemaphoreSlim capacity;
	readonly SemaphoreSlim growth = new(1, 1);
	readonly object sync = new();

	bool disposed;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="options"></param>
	/// <param name="loggerFactory"></param>
	public NodeEnginePool(IOptions<NodeEnginePoolOptions> options, ILoggerFactory loggerFactory)
		: this(options?.Value!, loggerFactory)
	{
	}

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="options"></param>
	/// <param name="loggerFactory"></param>
	public NodeEnginePool(NodeEnginePoolOptions options, ILoggerFactory loggerFactory)
	{
		this.options = options ?? throw new ArgumentNullException(nameof(options));
		this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

		// The cap is the whole pool's, so a single engine may take the lot while it is the only one.
		capacity = new SemaphoreSlim(options.EngineCount * options.MaxConcurrencyPerEngine);
	}

	/// <summary>
	/// Takes a lease on whichever engine is carrying the least work.
	/// </summary>
	/// <remarks>
	/// The lease must be disposed, whether or not anything was done with it, or its capacity never
	/// returns.
	/// </remarks>
	/// <param name="cancellationToken"></param>
	/// <exception cref="TimeoutException"></exception>
	public async Task<NodeEngineLease> AcquireAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(disposed, this);

		if (await capacity.WaitAsync(options.AcquireTimeout, cancellationToken) == false)
			throw new TimeoutException($"No capacity in the Node engine pool within {options.AcquireTimeout}.");

		NodeEngine? engine = null;

		try
		{
			engine = await SelectAsync(cancellationToken);
			return new NodeEngineLease(this, engine);
		}
		catch
		{
			if (engine is not null)
				Interlocked.Decrement(ref engine.InFlight);

			capacity.Release();
			throw;
		}
	}

	/// <summary>
	/// Acquires a lease, blocking while the pool grows an engine if it must.
	/// </summary>
	/// <remarks>
	/// The synchronous face of <see cref="AcquireAsync"/>: acquisition against a warm pool is
	/// immediate, and a cold one costs an engine's startup — a wait the caller has chosen to stand
	/// in rather than await.
	/// </remarks>
	public NodeEngineLease Acquire()
	{
		return AcquireAsync().GetAwaiter().GetResult();
	}

	/// <summary>
	/// Runs one piece of work against a module, on whichever engine is carrying the least, and
	/// returns the capacity when it completes.
	/// </summary>
	/// <remarks>
	/// The one-shot form: a single call needs no checkout ceremony. Take a lease instead when
	/// several steps must share one engine — successive one-shots may land on different engines,
	/// which different per-engine module state would notice — or when the claim must outlive the
	/// call, as a streamed result does.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="module"></param>
	/// <param name="work"></param>
	/// <param name="cancellationToken"></param>
	public async Task<T> RunAsync<T>(NodeModuleSource module, Func<Microsoft.JavaScript.NodeApi.JSValue, Task<T>> work, CancellationToken cancellationToken = default)
	{
		await using var lease = await AcquireAsync(cancellationToken);
		return await lease.RunAsync(module, work, cancellationToken);
	}

	/// <summary>
	/// Runs one piece of work on an engine's thread, module-free, and returns the capacity when it
	/// completes.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="work"></param>
	/// <param name="cancellationToken"></param>
	public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
	{
		await using var lease = await AcquireAsync(cancellationToken);
		return await lease.RunAsync(work);
	}

	/// <summary>
	/// Runs one piece of synchronous work against a module, blocking until it returns.
	/// </summary>
	/// <remarks>
	/// The synchronous one-shot, for work whose value is produced synchronously on the engine's
	/// thread. Promise-shaped work stays on <see cref="RunAsync{T}(NodeModuleSource, Func{Microsoft.JavaScript.NodeApi.JSValue, Task{T}}, CancellationToken)"/>:
	/// a promise settles when the engine's loop turns, which no blocked thread can wait out.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="module"></param>
	/// <param name="work"></param>
	public T Run<T>(NodeModuleSource module, Func<Microsoft.JavaScript.NodeApi.JSValue, T> work)
	{
		using var lease = Acquire();
		return lease.Run(module, work);
	}

	/// <summary>
	/// Runs one piece of synchronous work on an engine's thread, module-free, blocking until it
	/// returns.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="work"></param>
	public T Run<T>(Func<T> work)
	{
		using var lease = Acquire();
		return lease.Run(work);
	}

	/// <summary>
	/// Brings the pool to its configured size, running the given warmup against each engine.
	/// </summary>
	/// <remarks>
	/// Standing engines up and evaluating modules both stall the engine they run on, so doing it
	/// during startup keeps the cost out of whichever request happens to arrive first.
	/// </remarks>
	/// <param name="warm"></param>
	/// <param name="cancellationToken"></param>
	public async Task PrepareAsync(Func<NodeEngineLease, Task>? warm = null, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(disposed, this);

		while (Count < options.EngineCount)
			if (await GrowAsync(cancellationToken) is null)
				break;

		if (warm is null)
			return;

		foreach (var engine in Snapshot())
		{
			Interlocked.Increment(ref engine.InFlight);
			await using var lease = new NodeEngineLease(this, engine);
			await warm(lease);
		}
	}

	/// <summary>
	/// Returns a lease's capacity to the pool.
	/// </summary>
	/// <param name="engine"></param>
	internal void Release(NodeEngine engine)
	{
		Interlocked.Decrement(ref engine.InFlight);
		capacity.Release();
	}

	/// <summary>
	/// Selects an engine and charges it, standing up another where every existing one is already
	/// saturated and the configured size allows for it.
	/// </summary>
	/// <param name="cancellationToken"></param>
	async ValueTask<NodeEngine> SelectAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			var (best, load) = Least();

			// A free engine is always preferable to a new one: creating an engine costs a thread and
			// re-evaluating every module it will need, which is far more than queueing behind a
			// running one for the moment it takes to yield.
			if (best is not null && load < options.MaxConcurrencyPerEngine)
			{
				Interlocked.Increment(ref best.InFlight);
				return best;
			}

			if (Count < options.EngineCount && await GrowAsync(cancellationToken) is { } grown)
			{
				Interlocked.Increment(ref grown.InFlight);
				return grown;
			}

			// At full size and everything saturated. The pool-wide semaphore admitted this call, so
			// capacity is being released imminently; take the least-loaded engine rather than spin.
			if (best is not null)
			{
				Interlocked.Increment(ref best.InFlight);
				return best;
			}
		}
	}

	/// <summary>
	/// Adds one engine, unless another caller got there first or the pool is already full.
	/// </summary>
	/// <param name="cancellationToken"></param>
	async ValueTask<NodeEngine?> GrowAsync(CancellationToken cancellationToken)
	{
		await growth.WaitAsync(cancellationToken);

		try
		{
			if (Count >= options.EngineCount)
				return null;

			var logger = loggerFactory.CreateLogger<NodeEngine>();
			logger.LogDebug("Starting Node engine {Index} of {Count}.", Count + 1, options.EngineCount);

			var platform = NodeRuntimeHost.GetOrCreate(LibNodeLocator.Locate(options.LibNodePath));

			// Standing a runtime up is synchronous and takes a couple of hundred milliseconds, so it
			// is kept off whichever thread happened to ask for it.
			var engine = await Task.Run(() => new NodeEngine(platform, logger), cancellationToken);

			lock (sync)
				engines.Add(engine);

			return engine;
		}
		finally
		{
			growth.Release();
		}
	}

	/// <summary>
	/// Returns the least loaded engine along with its load, or null when none exist yet.
	/// </summary>
	(NodeEngine? Engine, int Load) Least()
	{
		lock (sync)
		{
			NodeEngine? best = null;
			var load = int.MaxValue;

			foreach (var engine in engines)
			{
				var current = Volatile.Read(ref engine.InFlight);
				if (current < load)
				{
					best = engine;
					load = current;
				}
			}

			return (best, load);
		}
	}

	/// <summary>
	/// Number of engines currently running.
	/// </summary>
	int Count
	{
		get { lock (sync) return engines.Count; }
	}

	/// <summary>
	/// Returns a stable copy of the current engines.
	/// </summary>
	NodeEngine[] Snapshot()
	{
		lock (sync) return [.. engines];
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (disposed)
			return;

		disposed = true;

		foreach (var engine in Snapshot())
			await engine.DisposeAsync();

		capacity.Dispose();
		growth.Dispose();
	}

}
