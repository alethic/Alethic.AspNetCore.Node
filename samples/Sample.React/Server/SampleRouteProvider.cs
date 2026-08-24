using Alethic.AspNetCore.Node;

using Microsoft.JavaScript.NodeApi;

namespace Sample.React.Server;

/// <summary>
/// Reads the sample application's routes out of the router it actually dispatches on.
/// </summary>
/// <remarks>
/// This is what an implementation for a real framework looks like, in miniature — and note how
/// little there is of it. Rendering needs nothing written at all: the application exposes a
/// <c>fetch</c> handler, so the stock <see cref="FetchRequestHandler"/> serves it unchanged, and
/// this sits beside that handler rather than wrapping it.
///
/// A real one would know React Router's build manifest, or TanStack Start's route tree, or
/// SvelteKit's manifest, in place of the <c>router</c> array this sample exposes. The shape of the
/// work is the same either way: reach into the running application, read the routes that are
/// already there, and translate them into <see cref="RenderRoute"/>.
/// </remarks>
sealed class SampleRouteProvider : INodeRouteProvider
{

	readonly NodeEnginePool pool;
	readonly NodeModuleSource module;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <remarks>
	/// The pool and the module, and nothing else — no request handler, no shared object. Node loads a module
	/// once per engine and caches it by resolved filename, so naming the same module as the request handler
	/// is the whole of what makes this read the very instance the request handler serves.
	/// </remarks>
	/// <param name="pool"></param>
	/// <param name="module"></param>
	public SampleRouteProvider(NodeEnginePool pool, NodeModuleSource module)
	{
		this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
		this.module = module ?? throw new ArgumentNullException(nameof(module));
	}

	/// <inheritdoc />
	/// <remarks>
	/// Runs on the engine's thread, against the prepared application. The entries are read straight
	/// off the JavaScript array — nothing is serialized on the way over — and only plain .NET data
	/// leaves the scope.
	/// </remarks>
	public Task<IReadOnlyList<RenderRoute>> GetRoutesAsync(CancellationToken cancellationToken = default) =>
		pool.RunAsync(module, exports =>
		{
			var table = NodeModuleExports.Default(exports)["router"];
			if (table.IsNullOrUndefined())
				throw new InvalidOperationException("The application exposes no router; the sample's provider cannot read its routes.");

			var routes = new List<RenderRoute>();
			var length = (int)table["length"];

			for (var i = 0; i < length; i++)
			{
				var entry = table[i];
				var render = entry["render"];

				routes.Add(new RenderRoute()
				{
					// The router's paths are already :param form, which is URLPattern pathname syntax.
					// A framework using its own grammar would be translated here instead.
					Pattern = (string)entry["path"],
					Id = (string)entry["id"],
					RenderMode = render.IsNullOrUndefined() == false && Enum.TryParse<RenderMode>((string)render, ignoreCase: true, out var parsed)
						? parsed
						: RenderMode.Server,
				});
			}

			return Task.FromResult<IReadOnlyList<RenderRoute>>(routes);
		}, cancellationToken);

}
