using System.Collections.Generic;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Configures a <see cref="FetchRequestHandler"/>.
/// </summary>
public class FetchRequestHandlerOptions
{

    /// <summary>
    /// The application's server module: a self-contained CommonJS bundle.
    /// </summary>
    public NodeModuleSource? Module { get; set; }

    /// <summary>
    /// Values the host supplies to the application, reaching it as the <c>env</c> argument of
    /// <c>fetch(request, env, ctx)</c>. The place for what only the host knows — an internal API
    /// address, an environment name.
    /// </summary>
    /// <remarks>
    /// Input only. The object is built fresh for each request, so an application keeps its own state
    /// in module scope, which Node caches per engine, rather than hanging it off <c>env</c>.
    ///
    /// Where Cloudflare puts live bindings here — KV namespaces, queues, secrets — this carries
    /// strings. The convention leaves the contents to the host; this host has strings to give.
    /// </remarks>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>();

}
