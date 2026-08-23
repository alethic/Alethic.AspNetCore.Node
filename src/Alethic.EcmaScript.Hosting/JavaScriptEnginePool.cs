using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Default pool: engines are created as demand requires them, and calls are placed on whichever
/// engine is carrying the least work.
/// </summary>
public sealed class JavaScriptEnginePool : IJavaScriptEnginePool
{

	readonly string name;
	readonly IJavaScriptEngineProvider provider;
	readonly JavaScriptEnginePoolOptions options;
	readonly ILogger logger;

	readonly List<Entry> entries = [];
	readonly SemaphoreSlim capacity;
	readonly SemaphoreSlim growth = new(1, 1);
	readonly object sync = new();

	bool disposed;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="name"></param>
	/// <param name="provider"></param>
	/// <param name="options"></param>
	/// <param name="logger"></param>
	public JavaScriptEnginePool(string name, IJavaScriptEngineProvider provider, JavaScriptEnginePoolOptions options, ILogger logger)
	{
		this.name = name ?? throw new ArgumentNullException(nameof(name));
		this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
		this.options = options ?? throw new ArgumentNullException(nameof(options));
		this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

		// The cap is the whole pool's, so a single engine may take the lot while it is the only one.
		capacity = new SemaphoreSlim(options.EngineCount * options.MaxConcurrencyPerEngine);
	}

	/// <inheritdoc />
	public string Name => name;

	/// <inheritdoc />
	public IJavaScriptModule GetModule(JavaScriptModuleSource source)
	{
		ArgumentNullException.ThrowIfNull(source);
		ObjectDisposedException.ThrowIf(disposed, this);

		return new JavaScriptPoolModule(this, source);
	}

	/// <inheritdoc />
	public async Task WarmAsync(JavaScriptModuleSource source, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		ObjectDisposedException.ThrowIf(disposed, this);

		// Bring the pool to full size, then evaluate on each. Doing this deliberately rather than
		// letting demand do it keeps the evaluation stall out of whichever request arrives first.
		while (Count < options.EngineCount)
			if (await GrowAsync(cancellationToken) is null)
				break;

		foreach (var entry in Snapshot())
			await entry.Engine.ImportAsync(source, cancellationToken);
	}

	/// <summary>
	/// Takes a slot on whichever engine is least busy, and evaluates the module there if it has not
	/// already been.
	/// </summary>
	/// <remarks>
	/// The slot is held until the caller disposes it, which for a streaming response means until that
	/// response has been read or abandoned rather than merely begun. Releasing when the head arrives
	/// would leave a render running against a slot the pool believes is free, and the configured
	/// concurrency would bound nothing.
	/// </remarks>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	/// <exception cref="TimeoutException"></exception>
	internal async Task<Slot> AcquireAsync(JavaScriptModuleSource source, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(disposed, this);

		if (await capacity.WaitAsync(options.AcquireTimeout, cancellationToken) == false)
			throw new TimeoutException($"No capacity in JavaScript engine pool '{name}' within {options.AcquireTimeout}.");

		Entry? entry = null;

		try
		{
			entry = await SelectAsync(cancellationToken);
			var instance = await entry.Engine.ImportAsync(source, cancellationToken);
			return new Slot(this, entry, instance);
		}
		catch
		{
			if (entry is not null)
				Interlocked.Decrement(ref entry.InFlight);

			capacity.Release();
			throw;
		}
	}

	/// <summary>
	/// Runs work against a module and releases the slot when it completes.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="source"></param>
	/// <param name="work"></param>
	/// <param name="cancellationToken"></param>
	internal async Task<T> InvokeAsync<T>(JavaScriptModuleSource source, Func<IJavaScriptModuleInstance, Task<T>> work, CancellationToken cancellationToken)
	{
		var slot = await AcquireAsync(source, cancellationToken);

		await using (slot)
			return await work(slot.Instance);
	}

	/// <summary>
	/// Returns a slot's capacity to the pool.
	/// </summary>
	/// <param name="entry"></param>
	void Release(Entry entry)
	{
		Interlocked.Decrement(ref entry.InFlight);
		capacity.Release();
	}

	/// <summary>
	/// Selects an engine and charges a slot to it, standing up another engine where every existing
	/// one is already saturated and the configured size allows for it.
	/// </summary>
	/// <param name="cancellationToken"></param>
	async ValueTask<Entry> SelectAsync(CancellationToken cancellationToken)
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

			// At full size and everything saturated. The pool-wide semaphore admitted this call, so a
			// slot is being released imminently; take the least-loaded engine rather than spin.
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
	async ValueTask<Entry?> GrowAsync(CancellationToken cancellationToken)
	{
		await growth.WaitAsync(cancellationToken);

		try
		{
			if (Count >= options.EngineCount)
				return null;

			logger.LogDebug("Starting engine {Index} of {Count} in JavaScript engine pool {Pool}.", Count + 1, options.EngineCount, name);
			var engine = await provider.CreateAsync(options, cancellationToken);
			var entry = new Entry(engine);

			lock (sync)
				entries.Add(entry);

			return entry;
		}
		finally
		{
			growth.Release();
		}
	}

	/// <summary>
	/// Returns the least loaded engine along with its load, or null when none exist yet.
	/// </summary>
	(Entry? Engine, int Load) Least()
	{
		lock (sync)
		{
			Entry? best = null;
			var load = int.MaxValue;

			foreach (var entry in entries)
			{
				var current = Volatile.Read(ref entry.InFlight);
				if (current < load)
				{
					best = entry;
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
		get { lock (sync) return entries.Count; }
	}

	/// <summary>
	/// Returns a stable copy of the current engines.
	/// </summary>
	Entry[] Snapshot()
	{
		lock (sync) return [.. entries];
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (disposed)
			return;

		disposed = true;

		foreach (var entry in Snapshot())
			await entry.Engine.DisposeAsync();

		capacity.Dispose();
		growth.Dispose();
	}

	/// <summary>
	/// An engine and the number of calls currently on it. A class rather than a struct because the
	/// counter is updated by interlocked operations on a shared field.
	/// </summary>
	internal sealed class Entry(IJavaScriptEngine engine)
	{

		public readonly IJavaScriptEngine Engine = engine;

		public int InFlight;

	}

	/// <summary>
	/// A claim on one engine's capacity, released on disposal.
	/// </summary>
	internal sealed class Slot : IAsyncDisposable
	{

		readonly JavaScriptEnginePool pool;
		readonly Entry entry;

		int released;

		/// <summary>
		/// Initializes a new instance.
		/// </summary>
		/// <param name="pool"></param>
		/// <param name="entry"></param>
		/// <param name="instance"></param>
		internal Slot(JavaScriptEnginePool pool, Entry entry, IJavaScriptModuleInstance instance)
		{
			this.pool = pool;
			this.entry = entry;
			Instance = instance;
		}

		/// <summary>
		/// The module, evaluated on the engine this slot is held against.
		/// </summary>
		public IJavaScriptModuleInstance Instance { get; }

		/// <inheritdoc />
		public ValueTask DisposeAsync()
		{
			// Idempotent: a streaming response releases on disposal, which a caller may do more than
			// once, and the pool must not gain capacity it never lost.
			if (Interlocked.Exchange(ref released, 1) == 0)
				pool.Release(entry);

			return ValueTask.CompletedTask;
		}

	}

}
