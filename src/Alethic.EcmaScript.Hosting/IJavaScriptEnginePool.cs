using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// A session against one engine of a pool: the module, evaluated there, and the engine itself.
/// </summary>
/// <remarks>
/// Handles are engine-affine, so work that holds them needs everything on one engine for its
/// duration — which is exactly what a session is. It also carries a unit of the pool's capacity,
/// returned on disposal; holding a session open is holding a claim on its engine.
/// </remarks>
public interface IJavaScriptSession : IAsyncDisposable
{

	/// <summary>
	/// The engine this session is pinned to.
	/// </summary>
	IJavaScriptEngine Engine { get; }

	/// <summary>
	/// The module, evaluated on this session's engine.
	/// </summary>
	IJavaScriptModuleInstance Module { get; }

}

/// <summary>
/// A named group of engines, and the dispatcher that decides which one serves a given session.
/// </summary>
/// <remarks>
/// A pool never hands ownership of an engine to a caller — a session claims capacity on one, not the
/// engine itself, and many sessions share an engine concurrently. That is what allows the number of
/// engines to change, including growing from the single engine a default configuration starts with,
/// without anything above having to know.
/// </remarks>
public interface IJavaScriptEnginePool : IAsyncDisposable
{

	/// <summary>
	/// Name this pool was registered under.
	/// </summary>
	string Name { get; }

	/// <summary>
	/// Opens a session for the given module on whichever engine is carrying the least work.
	/// </summary>
	/// <remarks>
	/// The module is evaluated on that engine if it has not been already; <see cref="WarmAsync"/>
	/// brings that cost forward. The session must be disposed, whether or not anything was done with
	/// it, or its capacity never returns.
	/// </remarks>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	Task<IJavaScriptSession> AcquireAsync(JavaScriptModuleSource source, CancellationToken cancellationToken = default);

	/// <summary>
	/// Ensures every engine in the pool has evaluated the given module.
	/// </summary>
	/// <remarks>
	/// Evaluation blocks the engine it runs on, so doing it during startup keeps the stall out of a
	/// request that happens to be first.
	/// </remarks>
	/// <param name="source"></param>
	/// <param name="cancellationToken"></param>
	Task WarmAsync(JavaScriptModuleSource source, CancellationToken cancellationToken = default);

}
