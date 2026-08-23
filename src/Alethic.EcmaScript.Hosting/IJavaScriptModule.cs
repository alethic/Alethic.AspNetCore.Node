using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// One module, dispatched across a pool. This is what consumers hold.
/// </summary>
/// <remarks>
/// Deliberately says nothing about which engine serves a call, so a pool may grow or shrink beneath
/// it. That freedom carries one obligation for the module itself: state held between calls is state
/// held per engine, so a module must treat anything outside a single call as either immutable or
/// absent. A module that caches across calls appears to work perfectly against one engine and turns
/// inconsistent the day a second appears.
/// </remarks>
public interface IJavaScriptModule
{

	/// <summary>
	/// The source this module dispatches to.
	/// </summary>
	JavaScriptModuleSource Source { get; }

	/// <summary>
	/// Invokes an exported function and converts its result from JSON.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="export">Dotted path through the module's exports.</param>
	/// <param name="arguments"></param>
	/// <param name="cancellationToken"></param>
	Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken);

	/// <summary>
	/// Invokes an exported function whose result is a stream of bytes with a structured prologue.
	/// </summary>
	/// <remarks>
	/// The call's engine capacity stays charged until the returned response is disposed, not merely
	/// until it is returned: the body is still being produced after this method completes, and
	/// releasing earlier would let concurrent streams pile onto an engine without bound.
	/// </remarks>
	/// <param name="export">Dotted path through the module's exports.</param>
	/// <param name="arguments"></param>
	/// <param name="payload"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptStreamResponse> InvokeStreamAsync(string export, object?[] arguments, ReadOnlyMemory<byte>? payload, CancellationToken cancellationToken);

}
