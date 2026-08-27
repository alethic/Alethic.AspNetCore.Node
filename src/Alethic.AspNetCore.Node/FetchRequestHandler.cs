using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JavaScript.NodeApi;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Renders by calling the application's <c>fetch</c> handler, on a Node engine pool.
/// </summary>
/// <remarks>
/// The stock <see cref="INodeRequestHandler"/>, and the one most applications need. The handler is called
/// as <c>fetch(request, env, ctx)</c>, which is the shape Cloudflare Workers defines and Deno, Bun,
/// and the framework adapters targeting them all follow. It is named for the handler rather than for
/// any export shape because no standard names the shape, and because a bare function as the default
/// export is accepted here too — what <c>createRequestHandler</c>-style factories produce, and a
/// leniency the convention itself does not have.
///
/// Conformance stops at the handler. The convention's other exports — <c>scheduled</c>,
/// <c>queue</c>, <c>tail</c> — are not called, this being an HTTP handler; and nothing is asked of
/// an application beyond <c>fetch</c>. Asynchronous per-engine startup is the application's own to
/// memoize in module scope, as it would be on Workers, where module scope is per isolate and here it
/// is per engine.
///
/// The module evaluates untouched: no adapter is appended and nothing is serialized on the way in
/// or out. The <c>Request</c> and <c>Response</c> objects are built and taken apart directly on the
/// engine's thread, where those types live.
///
/// A framework whose server protocol does not lower to a fetch handler gets its own handler
/// instead. Reading routes needs no handler of its own: pair an <see cref="INodeRouteProvider"/>
/// with this one.
///
/// Open to derivation, for the specialization that is a variation on this handler rather than a
/// protocol of its own: a host with something to add to the request, or to say about the response,
/// overrides <see cref="HandleAsync"/> and calls back. A handler speaking some other protocol
/// implements <see cref="INodeRequestHandler"/> directly instead — that is the seam for a different
/// conversation with the application, where this is the seam for the same one held differently.
/// </remarks>
public class FetchRequestHandler : INodeRequestHandler
{

    /// <summary>
    /// The execution context handed to the handler as its third argument.
    /// </summary>
    /// <remarks>
    /// <c>waitUntil</c> exists on Workers because the isolate is frozen once the response is
    /// returned, and work not registered with it is killed. A pooled engine is not frozen — it keeps
    /// running — so the promise proceeds whether or not anything registers it, and all this has to do
    /// is see that a rejection does not go unobserved. <c>passThroughOnException</c> is honestly a
    /// no-op: there is no origin behind this to pass a request through to.
    /// </remarks>
    const string ContextScript = """
        ({
            waitUntil(promise) {
                Promise.resolve(promise).catch(e =>
                    console.error('[Alethic.AspNetCore.Node] waitUntil rejected:', e));
            },
            passThroughOnException() { },
        })
        """;

    readonly NodeEnginePool pool;
    readonly NodeModuleSource module;
    readonly Uri baseUri;
    readonly Dictionary<string, string> environment;
    readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <remarks>
    /// Holds nothing beyond this. The module is Node's to load and cache; the environment is rebuilt
    /// per request from these values, being input rather than a place to keep things. An application
    /// needing asynchronous startup memoizes it in module scope, which is cached per engine because
    /// that is what a module is — the same thing a Workers application does for per-isolate setup.
    ///
    /// The logger is optional so that the ordinary case reads as one <c>new</c>.
    /// </remarks>
    /// <param name="pool"></param>
    /// <param name="options"></param>
    /// <param name="logger"></param>
    public FetchRequestHandler(NodeEnginePool pool, FetchRequestHandlerOptions options, ILogger<FetchRequestHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
        this.logger = logger ?? NullLogger<FetchRequestHandler>.Instance;

        module = options.Module ?? throw new ArgumentException("A handler needs a module.", nameof(options));
        baseUri = options.BaseUri ?? throw new ArgumentException("A handler needs a base address.", nameof(options));
        environment = new Dictionary<string, string>(options.Environment);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Loads the module on every engine, so that evaluating it — which occupies an engine's event
    /// loop — happens at startup rather than under whichever request arrives first. A module that
    /// cannot be loaded fails here, and with it the deployment.
    /// </remarks>
    public virtual Task PrepareAsync(CancellationToken cancellationToken = default) =>
        pool.PrepareAsync(lease => lease.ImportAsync(module, cancellationToken), cancellationToken);

    /// <summary>
    /// Builds the environment object the handler receives. Must be called on the engine's thread.
    /// </summary>
    JSValue BuildEnvironment()
    {
        var env = JSValue.CreateObject();
        foreach (var (key, value) in environment)
            env[key] = value;

        return env;
    }

    /// <summary>
    /// Headers stating where the application is mounted, which this handler writes from the request
    /// ASP.NET resolved rather than passing on whatever arrived under those names.
    /// </summary>
    static readonly string[] MountHeaders = ["X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix"];

    /// <summary>
    /// Response headers the server frames itself, whatever the application says about them.
    /// </summary>
    static readonly string[] FramingHeaders = ["Content-Length", "Transfer-Encoding"];

    /// <inheritdoc />
    public virtual async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cancellationToken = context.RequestAborted;

        // The path below the mount, resolved against the address the application is asked at. This is
        // a proxy — an in-process one rather than a network one — so the application is addressed the
        // way an origin server behind one is: the mount removed from the path and named in a header,
        // and nothing in the URL claiming to be where the caller was.
        //
        // Concatenated rather than resolved: the request path is rooted, and Uri resolution treats a
        // rooted reference as absolute, which would drop any path the base address carries instead of
        // inserting it.
        var url = string.Concat(
            baseUri.GetLeftPart(UriPartial.Authority),
            baseUri.AbsolutePath.TrimEnd('/'),
            context.Request.Path.ToUriComponent(),
            context.Request.QueryString.ToUriComponent());
        var method = context.Request.Method;
        var headers = CollectHeaders(context);

        byte[]? body = null;
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken);
            body = buffer.ToArray();
        }

        var lease = await pool.AcquireAsync(cancellationToken);
        var pipe = new Pipe();
        var head = new TaskCompletionSource<ResponseHead>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pump = null;

        try
        {
            // The render runs ahead of the copy: the head settles as soon as the application answers,
            // and the body arrives behind it. A fault before the head surfaces here; one after it can
            // only truncate the body, the status having already gone out.
            pump = PumpAsync(lease, url, method, headers, body, pipe.Writer, head, cancellationToken);

            var completed = await Task.WhenAny(head.Task, pump);
            if (completed == pump)
                await pump; // faulted before producing a head; observe the exception

            var headValue = await head.Task;

            context.Response.StatusCode = headValue.Status;

            foreach (var (name, value) in headValue.Headers)
            {
                // The body arrives as a stream of unknown length and the server frames it itself; a
                // length or transfer coding from the application would claim otherwise.
                if (FramingHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                context.Response.Headers.Append(name, value);
            }

            await CopyAsync(pipe.Reader, context.Response.Body, cancellationToken);
        }
        finally
        {
            // In this order: completing the reader is what unblocks a pump still writing, so it has
            // to happen before the pump is waited on; the pump's fault, if any, was already delivered
            // through the head or the body and is only observed here; and the lease is released last,
            // once nothing is still running against the engine.
            await pipe.Reader.CompleteAsync();

            if (pump is not null)
            {
                try
                {
                    await pump;
                }
                catch
                {
                }
            }

            await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// Copies the rendered body to the response as it is produced.
    /// </summary>
    /// <remarks>
    /// Flushed per read rather than copied wholesale, so progress the render makes is progress the
    /// client sees — which is the whole point of a shell reaching the browser ahead of the content
    /// suspended behind it.
    /// </remarks>
    /// <param name="reader"></param>
    /// <param name="destination"></param>
    /// <param name="cancellationToken"></param>
    static async Task CopyAsync(PipeReader reader, Stream destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken);

            foreach (var segment in result.Buffer)
            {
                await destination.WriteAsync(segment, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            reader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                break;
        }
    }

    /// <summary>
    /// Dispatches the request on the lease's engine and drains the response body into the pipe.
    /// </summary>
    /// <remarks>
    /// One trip onto the engine's thread for the whole render. Everything held across an await goes
    /// through a <see cref="JSReference"/>, awaiting being what ends a value's scope; the abort
    /// controller is additionally posted back rather than called from the cancellation thread,
    /// because that is where it lives.
    /// </remarks>
    /// <param name="lease"></param>
    /// <param name="url"></param>
    /// <param name="method"></param>
    /// <param name="headers"></param>
    /// <param name="body"></param>
    /// <param name="writer"></param>
    /// <param name="head"></param>
    /// <param name="cancellationToken"></param>
    async Task PumpAsync(NodeEngineLease lease, string url, string method, List<KeyValuePair<string, string>> headers, byte[]? body, PipeWriter writer, TaskCompletionSource<ResponseHead> head, CancellationToken cancellationToken)
    {
        try
        {
            await lease.RunAsync(module, async exports =>
            {
                // One leniency the convention does not have: a bare function as the default export is
                // the fetch handler itself, which is what createRequestHandler-style factories produce.
                var app = NodeModuleExports.Default(exports);
                var fetch = app.IsFunction() ? app : app.IsNullOrUndefined() ? app : app["fetch"];
                if (fetch.IsFunction() == false)
                    throw new InvalidOperationException($"Module '{module.Name}' has no default export with a fetch function.");

                var controller = JSValue.RunScript("new AbortController()");
                using var controllerRef = new JSReference(controller, isWeak: false);
                using var registration = cancellationToken.Register(() => lease.TryPost(
                    () => controllerRef.GetValue().CallMethod("abort", "the request was aborted")));

                var request = BuildRequest(url, method, headers, body, controller["signal"]);
                // Three arguments, as the convention specifies: the request, the host environment, and an
                // execution context. A handler reaching for ctx.waitUntil finds it rather than undefined.
                var pending = fetch.Call(app.IsFunction() ? JSValue.Undefined : app, request, BuildEnvironment(), JSValue.RunScript(ContextScript));

                // Scope ends at the await; everything above but the references is invalid after it.
                var response = await ((JSPromise)JSValue.Global["Promise"].CallMethod("resolve", pending)).AsTask();

                head.TrySetResult(new ResponseHead((int)response["status"], CollectResponseHeaders(response)));

                var stream = response["body"];
                if (stream.IsNullOrUndefined())
                    return 0;

                using var reader = new JSReference(stream.CallMethod("getReader"), isWeak: false);

                while (true)
                {
                    var chunk = await ((JSPromise)reader.GetValue().CallMethod("read")).AsTask();
                    if ((bool)chunk["done"])
                        break;

                    // Copied into .NET memory while still inside the scope that produced it.
                    var copied = ((JSTypedArray<byte>)chunk["value"]).Span.ToArray();
                    var flushed = await writer.WriteAsync(copied, CancellationToken.None);
                    if (flushed.IsCompleted)
                        break; // the consumer gave up on the body
                }

                return 0;
            }, cancellationToken);

            await writer.CompleteAsync();
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Render of module {Module} failed.", module.Name);
            head.TrySetException(e);
            await writer.CompleteAsync(e);
        }
    }

    /// <summary>
    /// Builds the runtime's own <c>Request</c>, value by value. Must be called on the engine's thread.
    /// </summary>
    /// <param name="url"></param>
    /// <param name="method"></param>
    /// <param name="headers"></param>
    /// <param name="body"></param>
    /// <param name="signal"></param>
    static JSValue BuildRequest(string url, string method, List<KeyValuePair<string, string>> headers, byte[]? body, JSValue signal)
    {
        var init = JSValue.CreateObject();
        init["method"] = method;
        init["signal"] = signal;

        // Headers in fetch's pair form: an array of [name, value] arrays.
        var pairs = JSValue.CreateArray(headers.Count);
        for (var i = 0; i < headers.Count; i++)
        {
            var pair = JSValue.CreateArray(2);
            pair[0] = headers[i].Key;
            pair[1] = headers[i].Value;
            pairs[i] = pair;
        }

        init["headers"] = pairs;

        if (body is not null)
            init["body"] = new JSTypedArray<byte>(body);

        return JSValue.Global["Request"].CallAsConstructor(url, init);
    }

    /// <summary>
    /// Reads the response's headers out through their own iterator. Must be called on the engine's
    /// thread.
    /// </summary>
    /// <param name="response"></param>
    static List<KeyValuePair<string, string>> CollectResponseHeaders(JSValue response)
    {
        var headers = new List<KeyValuePair<string, string>>();
        var entries = response["headers"].CallMethod("entries");

        while (true)
        {
            var step = entries.CallMethod("next");
            if ((bool)step["done"])
                break;

            var pair = step["value"];
            headers.Add(new((string)pair[0], (string)pair[1]));
        }

        return headers;
    }

    /// <summary>
    /// Flattens the request's headers, and states where the application is mounted.
    /// </summary>
    /// <remarks>
    /// <c>Host</c> is dropped: the authority the application is addressed at is in the URL, and a
    /// Host header beside it would contradict it.
    ///
    /// The mount headers are dropped and then written afresh. Every other header is passed on as it
    /// arrived, so leaving these would let a caller describe the mount to the application and have it
    /// read as though this host had said so.
    /// </remarks>
    /// <param name="context"></param>
    static List<KeyValuePair<string, string>> CollectHeaders(HttpContext context)
    {
        var headers = new List<KeyValuePair<string, string>>();

        foreach (var header in context.Request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            if (MountHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                continue;

            foreach (var value in header.Value)
                if (value is not null)
                    headers.Add(new(header.Key, value));
        }

        headers.Add(new("X-Forwarded-Proto", context.Request.Scheme));

        if (context.Request.Host.HasValue)
            headers.Add(new("X-Forwarded-Host", context.Request.Host.Value));

        // Absent rather than empty at the root, which is what a proxy rewriting no prefix sends, and
        // which leaves the application's own default as the answer rather than a case to handle.
        if (context.Request.PathBase.HasValue)
            headers.Add(new("X-Forwarded-Prefix", context.Request.PathBase.Value));

        return headers;
    }

    /// <summary>
    /// The part of a response known before its body has arrived.
    /// </summary>
    /// <param name="Status"></param>
    /// <param name="Headers"></param>
    readonly record struct ResponseHead(int Status, List<KeyValuePair<string, string>> Headers);

}
