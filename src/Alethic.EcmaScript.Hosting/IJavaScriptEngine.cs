using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// A single JavaScript runtime: one isolated world, executing on one thread.
/// </summary>
/// <remarks>
/// An engine evaluates modules and hands back handles to what they export; it has no opinions about
/// what runs on it. Engines exist for parallelism, since JavaScript executes on one thread and
/// throughput past a single core needs more than one of them. They are not an isolation boundary
/// between concurrent calls: one engine services many overlapping calls quite happily, because
/// everything a module awaits yields to the event loop.
/// </remarks>
public interface IJavaScriptEngine : IAsyncDisposable
{

	/// <summary>
	/// Evaluates a module on this engine, or returns the instance already evaluated for this source.
	/// </summary>
	/// <remarks>
	/// Evaluation runs synchronously inside the runtime and takes appreciable time for a real bundle,
	/// so it occupies this engine's event loop and stalls whatever is already in flight on it. Warming
	/// a module ahead of first use is what keeps that cost out of a request.
	/// </remarks>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	ValueTask<IJavaScriptModuleInstance> ImportAsync(JavaScriptModuleSource source, CancellationToken cancellationToken = default);

	/// <summary>
	/// Evaluates an expression and returns its value.
	/// </summary>
	/// <param name="script"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptValue> EvaluateAsync(string script, CancellationToken cancellationToken = default);

	/// <summary>
	/// Creates a <c>Uint8Array</c> in the engine holding a copy of the given bytes.
	/// </summary>
	/// <param name="bytes"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptValue> CreateByteArrayAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

}
