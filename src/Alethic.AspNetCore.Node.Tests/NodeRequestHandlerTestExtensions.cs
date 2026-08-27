using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

namespace Alethic.AspNetCore.Node.Tests;

/// <summary>
/// Drives a handler the way an endpoint does, and reads back what it answered.
/// </summary>
/// <remarks>
/// A handler writes into an <see cref="HttpContext"/>, so a test needs one to give it. Assembling
/// the result back into a response message keeps each test about what was answered rather than about
/// the plumbing that carried it.
/// </remarks>
static class NodeRequestHandlerTestExtensions
{

    /// <summary>
    /// Answers one request, through a context built for the purpose.
    /// </summary>
    /// <param name="handler"></param>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    public static async Task<HttpResponseMessage> SendAsync(this INodeRequestHandler handler, HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        var context = new DefaultHttpContext();

        context.Request.Method = request.Method.Method;
        context.Request.Scheme = uri.Scheme;
        context.Request.Host = new HostString(uri.Authority);
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        context.RequestAborted = cancellationToken;

        foreach (var header in request.Headers)
            context.Request.Headers[header.Key] = header.Value.ToArray();

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
                context.Request.Headers[header.Key] = header.Value.ToArray();

            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = body.Length;
        }

        var written = new MemoryStream();
        context.Response.Body = written;

        await handler.HandleAsync(context);

        var response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(written.ToArray()),
        };

        foreach (var header in context.Response.Headers)
            if (response.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) == false)
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());

        return response;
    }

}
