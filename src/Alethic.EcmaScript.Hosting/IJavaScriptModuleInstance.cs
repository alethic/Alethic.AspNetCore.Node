using System.Net.Http;
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
	/// Dispatches a request to the module's default <c>fetch</c> export.
	/// </summary>
	/// <remarks>
	/// The response streams. It is returned as soon as the module answers with one, and its content
	/// continues to fill afterwards, so a caller wanting the whole body must read it to the end. Note
	/// that a failure arising after the first byte cannot alter the status line, since by then it has
	/// already been committed.
	/// </remarks>
	/// <param name="request"></param>
	/// <param name="cancellationToken">Aborts the render itself, not merely the wait for it.</param>
	Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);

	/// <summary>
	/// Invokes an exported function and converts its result from JSON.
	/// </summary>
	/// <remarks>
	/// The escape hatch for work that is not a request, such as enumerating routes or gathering the
	/// data behind a sitemap. Arguments and results cross the boundary as JSON.
	/// </remarks>
	/// <typeparam name="T"></typeparam>
	/// <param name="export"></param>
	/// <param name="arguments"></param>
	/// <param name="cancellationToken"></param>
	Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken);

}
