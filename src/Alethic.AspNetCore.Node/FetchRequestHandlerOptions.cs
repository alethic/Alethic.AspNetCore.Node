using System;
using System.Collections.Generic;

using Microsoft.AspNetCore.Http;

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
    /// The address the application is asked at, which the path below the mount is resolved against.
    /// </summary>
    /// <remarks>
    /// Not where the caller was, and deliberately not able to pass for it. The default authority is
    /// reserved by RFC 2606 against ever resolving, so no deployment can make it look plausible and
    /// nothing can quietly take a request URL for its own public address — <c>X-Forwarded-Proto</c>,
    /// <c>X-Forwarded-Host</c> and <c>X-Forwarded-Prefix</c> are the account of that, and an
    /// application is meant to have to read them.
    ///
    /// A path here is inserted ahead of the request's own, for an application that expects to be
    /// asked under one. It is unrelated to where the host has mounted the application, which is
    /// removed from the path and named in <c>X-Forwarded-Prefix</c> instead.
    /// </remarks>
    public Uri BaseUri { get; set; } = new Uri("http://node.invalid/");

    /// <summary>
    /// How the request body reaches the application. Streamed by default.
    /// </summary>
    /// <remarks>
    /// Buffered where the application needs to read the body more than once, which a stream does not
    /// allow — cloning a request, or retrying a parse. It costs the body in memory.
    /// </remarks>
    public BodyMode RequestBody { get; set; } = BodyMode.Streamed;

    /// <summary>
    /// How the rendered response reaches the client. Streamed by default.
    /// </summary>
    /// <remarks>
    /// Buffered where a render that fails partway through should fail rather than truncate: nothing
    /// is written until it has finished, so the status is still open when the fault arrives. It also
    /// gives the response a length instead of chunked framing. A render that waits on all its data
    /// before answering gives up nothing by it.
    /// </remarks>
    public BodyMode ResponseBody { get; set; } = BodyMode.Streamed;

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

    /// <summary>
    /// Adds to the environment for one request.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment"/> is the bindings an application is deployed with, fixed for as long
    /// as the handler lives — which is what the convention describes, those being per isolate on
    /// Workers and per engine here. This is for what a host knows per request and cannot state any
    /// other way: a tenant resolved from the authority, a correlation id, a flag set. Called with the
    /// static values already in place, so it may add to them or replace them.
    ///
    /// Not the place for what the request already carries. Anything the protocol states — the method,
    /// the path, the caller's address — reaches the application on the request itself, and saying it
    /// twice invites the two accounts to disagree.
    ///
    /// Runs off the engine's thread, before the render begins, so it may do as it likes without
    /// occupying an event loop.
    /// </remarks>
    public Action<HttpContext, IDictionary<string, string>>? ConfigureEnvironment { get; set; }

}
