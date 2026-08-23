using System.Net.Http;
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
public interface IJavaScriptApplication
{

	/// <summary>
	/// The module this application dispatches to.
	/// </summary>
	JavaScriptModuleSource Source { get; }

	/// <summary>
	/// Dispatches a request to the module's default <c>fetch</c> export.
	/// </summary>
	/// <param name="request"></param>
	/// <param name="cancellationToken">Aborts the render itself, not merely the wait for it.</param>
	Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);

	/// <summary>
	/// Invokes an exported function and converts its result from JSON.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	/// <param name="export"></param>
	/// <param name="arguments"></param>
	/// <param name="cancellationToken"></param>
	Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken);

}
