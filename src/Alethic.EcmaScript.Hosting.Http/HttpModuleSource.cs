using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// Decorates a module with the glue that adapts its fetch export to the general streaming call.
/// </summary>
/// <remarks>
/// The <c>Request</c> and <c>Response</c> types are the runtime's, so the place to construct and
/// take them apart is inside the runtime rather than from the host — which is what lets the hosting
/// and backend layers stay ignorant of HTTP altogether. The glue is appended to the module text and
/// evaluates in the same CommonJS scope, where it wraps the module's default export behind an
/// additional export shaped for <see cref="IJavaScriptModuleInstance.InvokeStreamAsync"/>.
/// </remarks>
sealed class HttpModuleSource : JavaScriptModuleSource
{

	/// <summary>
	/// Name of the export the glue adds.
	/// </summary>
	public const string HandleExport = "__alethicHttpHandle";

	/// <summary>
	/// The glue itself, following the streaming call convention: JSON arguments, then the payload,
	/// then the signal, answering <c>{ head, body }</c>.
	/// </summary>
	const string Glue = """

		// --- appended by Alethic.EcmaScript.Hosting.Http ---
		module.exports.__alethicHttpHandle = function (req, payload, signal) {
			const app = module.exports.default;
			const request = new Request(req.url, {
				method: req.method,
				headers: req.headers,
				body: payload ?? undefined,
				signal: signal,
			});
			return Promise.resolve(app.fetch(request)).then(response => ({
				head: {
					status: response.status,
					headers: Array.from(response.headers.entries()),
				},
				body: response.body,
			}));
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
