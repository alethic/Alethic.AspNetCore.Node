using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// A module that has been evaluated on one particular engine.
/// </summary>
/// <remarks>
/// Bound to the engine that evaluated it. Every member marshals onto that engine's JavaScript thread
/// and hands back only .NET data: no runtime value may cross the boundary, because such a value is
/// valid solely within the scope that produced it, and a scope ends at the first await.
/// </remarks>
public interface IJavaScriptModuleInstance
{

	/// <summary>
	/// The source this instance was evaluated from.
	/// </summary>
	JavaScriptModuleSource Source { get; }

	/// <summary>
	/// Invokes an exported function and converts its result from JSON.
	/// </summary>
	/// <remarks>
	/// The export is named by a dotted path through the module's exports, <c>default.routes</c> for
	/// example, and is called on the object that holds it so its <c>this</c> is what the module
	/// expects. Arguments and results cross the boundary as JSON.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="export"></param>
	/// <param name="arguments"></param>
	/// <param name="cancellationToken"></param>
	Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken);

	/// <summary>
	/// Invokes an exported function whose result is a stream of bytes with a structured prologue.
	/// </summary>
	/// <remarks>
	/// The export is called as <c>export(...arguments, payload, signal)</c>: the JSON arguments
	/// spread first, then the payload bytes as a <c>Uint8Array</c> or null, then an
	/// <c>AbortSignal</c> wired to the cancellation token. It returns, or resolves to, an object of
	/// the form <c>{ head, body }</c> — <c>head</c> any JSON-serializable value, <c>body</c> a
	/// <c>ReadableStream</c> of bytes or absent.
	///
	/// The result is returned once the head is known, and the body continues to fill as the module
	/// produces it. Cancellation reaches the module through the signal, aborting the work itself
	/// rather than merely the wait for it.
	/// </remarks>
	/// <param name="export"></param>
	/// <param name="arguments"></param>
	/// <param name="payload"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptStreamResponse> InvokeStreamAsync(string export, object?[] arguments, ReadOnlyMemory<byte>? payload, CancellationToken cancellationToken);

}
