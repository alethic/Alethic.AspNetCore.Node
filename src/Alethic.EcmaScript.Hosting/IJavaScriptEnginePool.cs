using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// A named group of engines, and the dispatcher that decides which one runs a given call.
/// </summary>
/// <remarks>
/// A pool never hands ownership of an engine to a caller. That is what allows the number of engines
/// to change — including growing from the single engine a default configuration starts with — without
/// anything above having to know.
/// </remarks>
public interface IJavaScriptEnginePool : IAsyncDisposable
{

	/// <summary>
	/// Name this pool was registered under.
	/// </summary>
	string Name { get; }

	/// <summary>
	/// Returns a handle that dispatches calls for the given module across this pool.
	/// </summary>
	/// <remarks>
	/// The module is not evaluated here. Each engine evaluates it the first time that engine is asked
	/// to serve it, and reuses it thereafter. <see cref="WarmAsync"/> brings that cost forward.
	/// </remarks>
	/// <param name="source"></param>
	IJavaScriptApplication GetApplication(JavaScriptModuleSource source);

	/// <summary>
	/// Ensures every engine in the pool has evaluated the given module.
	/// </summary>
	/// <remarks>
	/// Evaluation blocks the engine it runs on, so doing it during startup keeps the stall out of a
	/// request that happens to be first.
	/// </remarks>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	Task WarmAsync(JavaScriptModuleSource source, CancellationToken cancellationToken);

}
