using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.JavaScript.NodeApi;

namespace Alethic.AspNetCore.EcmaScript.Node;

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
/// Disposal returns the capacity, nothing more. Work already posted to the engine runs to
/// completion regardless, which is what lets a caller start something long-lived and release its
/// claim by another route.
/// </remarks>
public sealed class NodeEngineLease : IAsyncDisposable
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

		var exports = await engine.ImportAsync(module, cancellationToken);
		return await engine.Runtime.RunAsync(() => work(exports.GetValue()));
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
	/// Posts fire-and-forget work to the engine's thread, quietly dropping it if the engine is gone.
	/// </summary>
	/// <param name="action"></param>
	public void TryPost(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);

		engine.TryPost(action);
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		// Idempotent: disposal may arrive from more than one owner, and the pool must not gain
		// capacity it never lost.
		if (Interlocked.Exchange(ref released, 1) == 0)
			pool.Release(engine);

		return ValueTask.CompletedTask;
	}

}
