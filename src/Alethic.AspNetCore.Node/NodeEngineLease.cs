using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.JavaScript.NodeApi;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// A claim on one engine's capacity, and the way onto its thread.
/// </summary>
/// <remarks>
/// Code inside <see cref="RunAsync{T}(Func{Task{T}})"/> executes on the engine's JavaScript thread
/// and writes ordinary node-api-dotnet — <see cref="JSValue"/>, <see cref="JSPromise"/>,
/// <see cref="JSReference"/> — under that library's own rules. The two that matter most: a
/// <see cref="JSValue"/> is valid only within the scope that produced it, and awaiting ends that
/// scope even without leaving the thread, so anything held across an await goes through a
/// <see cref="JSReference"/>; and nothing produced inside may escape except as plain .NET data.
///
/// Every promise-shaped member has an async form only: a promise settles when the engine's loop
/// turns, and a thread blocked inside a synchronous callback is exactly what stops it turning. The
/// synchronous twins — <see cref="Run{T}(Func{T})"/> and friends — exist for work whose value is
/// produced synchronously on the engine's thread, where they save a task's ceremony.
///
/// Disposal returns the capacity, nothing more. Work already posted to the engine runs to
/// completion regardless, which is what lets a caller start something long-lived and release its
/// claim by another route.
/// </remarks>
public sealed class NodeEngineLease : IDisposable, IAsyncDisposable
{

	readonly NodeEnginePool pool;
	readonly NodeEngine engine;

	int released;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="pool"></param>
	/// <param name="engine"></param>
	internal NodeEngineLease(NodeEnginePool pool, NodeEngine engine)
	{
		this.pool = pool;
		this.engine = engine;
	}

	/// <summary>
	/// The engine this lease is held against, for per-engine bookkeeping within the package.
	/// </summary>
	internal NodeEngine Engine => engine;

	/// <summary>
	/// Runs work on the engine's JavaScript thread.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="work"></param>
	public Task<T> RunAsync<T>(Func<Task<T>> work)
	{
		ArgumentNullException.ThrowIfNull(work);

		return engine.Runtime.RunAsync(work);
	}

	/// <summary>
	/// Runs work on the engine's JavaScript thread against a module's exports, evaluating the module
	/// on this engine first if it has not been already.
	/// </summary>
	/// <remarks>
	/// The exports value handed to the callback is valid for the callback's initial scope; as ever,
	/// holding it across an await means taking a <see cref="JSReference"/> to it first.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="module"></param>
	/// <param name="work"></param>
	/// <param name="cancellationToken"></param>
	public async Task<T> RunAsync<T>(NodeModuleSource module, Func<JSValue, Task<T>> work, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(module);
		ArgumentNullException.ThrowIfNull(work);

		return await engine.RunAsync(module, work, cancellationToken);
	}

	/// <summary>
	/// Runs synchronous work on the engine's JavaScript thread, blocking until it returns.
	/// </summary>
	/// <remarks>
	/// For work whose value exists before the callback returns. A promise made inside still settles
	/// afterward — the loop resumes the moment the callback is done — but its result is not
	/// available here; work that must observe one belongs on <see cref="RunAsync{T}(Func{Task{T}})"/>.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="work"></param>
	public T Run<T>(Func<T> work)
	{
		ArgumentNullException.ThrowIfNull(work);

		return engine.Runtime.Run(work);
	}

	/// <summary>
	/// Runs synchronous work on the engine's JavaScript thread against a module's exports,
	/// evaluating the module on this engine first if it has not been already.
	/// </summary>
	/// <remarks>
	/// CommonJS evaluation is itself synchronous, so the whole call is a real synchronous dispatch —
	/// only a source still being read from disk makes it wait on anything else.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="module"></param>
	/// <param name="work"></param>
	public T Run<T>(NodeModuleSource module, Func<JSValue, T> work)
	{
		ArgumentNullException.ThrowIfNull(module);
		ArgumentNullException.ThrowIfNull(work);

		return engine.Run(module, work);
	}

	/// <summary>
	/// Evaluates a module on this engine ahead of use.
	/// </summary>
	/// <param name="module"></param>
	/// <param name="cancellationToken"></param>
	public Task ImportAsync(NodeModuleSource module, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(module);

		return engine.ImportAsync(module, cancellationToken);
	}

	/// <summary>
	/// Evaluates a module on this engine ahead of use, blocking until its exports exist.
	/// </summary>
	/// <param name="module"></param>
	public void Import(NodeModuleSource module)
	{
		ArgumentNullException.ThrowIfNull(module);

		engine.ImportAsync(module, CancellationToken.None).GetAwaiter().GetResult();
	}

	/// <summary>
	/// Posts fire-and-forget work to the engine's thread, quietly dropping it if the engine is gone.
	/// </summary>
	/// <param name="action"></param>
	public void TryPost(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);

		engine.TryPost(action);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		// Idempotent: disposal may arrive from more than one owner, and the pool must not gain
		// capacity it never lost.
		if (Interlocked.Exchange(ref released, 1) == 0)
			pool.Release(engine);
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		Dispose();
		return ValueTask.CompletedTask;
	}

}
