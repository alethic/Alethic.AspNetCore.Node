using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// Decorates a module with the glue that adapts its fetch export to something the host can drive
/// through plain object operations.
/// </summary>
/// <remarks>
/// The <c>Request</c> and <c>Response</c> types are the runtime's, so the place to construct and
/// take them apart is inside the runtime rather than from the host — which is what lets the hosting
/// and backend layers stay ignorant of HTTP altogether. That the request and head travel as JSON
/// text is likewise this layer's own choice, made here in its glue; the layers below neither know
/// nor care. The glue is appended to the module text and evaluates in the same CommonJS scope,
/// where it adds exports alongside the module's own.
/// </remarks>
sealed class HttpModuleSource : JavaScriptModuleSource
{

	/// <summary>
	/// Name of the request-handling export the glue adds.
	/// </summary>
	public const string HandleExport = "__alethicHttpHandle";

	/// <summary>
	/// Name of the manifest export the glue adds.
	/// </summary>
	public const string RoutesExport = "__alethicHttpRoutes";

	/// <summary>
	/// The glue itself. <c>handle</c> takes the request as JSON text, the body bytes, and an abort
	/// signal, and answers <c>{ head, body }</c> — the head as JSON text, the body as the response's
	/// own stream. <c>routes</c> asks the application for its manifest by export name and answers it
	/// as JSON text, or null where the application offers none.
	/// </summary>
	const string Glue = """

		// --- appended by Alethic.EcmaScript.Hosting.Http ---
		module.exports.__alethicHttpHandle = function (requestJson, payload, signal) {
			const app = module.exports.default;
			const req = JSON.parse(requestJson);
			const request = new Request(req.url, {
				method: req.method,
				headers: req.headers,
				body: payload ?? undefined,
				signal: signal,
			});
			return Promise.resolve(app.fetch(request)).then(response => ({
				head: JSON.stringify({
					status: response.status,
					headers: Array.from(response.headers.entries()),
				}),
				body: response.body,
			}));
		};
		module.exports.__alethicHttpRoutes = function (name) {
			const app = module.exports.default;
			const fn = app ? app[name] : undefined;
			if (typeof fn !== 'function')
				return null;
			return Promise.resolve(fn.call(app)).then(routes => routes == null ? null : JSON.stringify(routes));
		};
		""";

	readonly JavaScriptModuleSource inner;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="inner"></param>
	public HttpModuleSource(JavaScriptModuleSource inner)
	{
		this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
	}

	/// <inheritdoc />
	public override string Key => "http:" + inner.Key;

	/// <inheritdoc />
	public override string Name => inner.Name;

	/// <inheritdoc />
	public override async ValueTask<string> ReadAsync(CancellationToken cancellationToken) =>
		await inner.ReadAsync(cancellationToken) + Glue;

}
