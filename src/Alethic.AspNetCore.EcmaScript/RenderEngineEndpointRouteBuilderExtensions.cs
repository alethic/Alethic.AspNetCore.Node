using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Alethic.AspNetCore.EcmaScript;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Mounts rendering engines into the endpoint table.
/// </summary>
public static class RenderEngineEndpointRouteBuilderExtensions
{

	/// <summary>
	/// Maps a single pattern to a rendering engine.
	/// </summary>
	/// <param name="endpoints"></param>
	/// <param name="pattern"></param>
	/// <param name="engine"></param>
	public static IEndpointConventionBuilder MapRenderEngine(this IEndpointRouteBuilder endpoints, string pattern, IRenderEngine engine)
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(engine);

		return endpoints.Map(pattern, context => DispatchAsync(context, engine));
	}

	/// <summary>
	/// Mounts a rendering engine: prepares it, reads its route manifest, and maps an endpoint per
	/// route plus a fallback.
	/// </summary>
	/// <remarks>
	/// Awaited before the server starts, deliberately: the prepared engine is what answers the
	/// manifest query, so the route table is the running application's rather than a copy that could
	/// drift, and the preparation cost lands at startup instead of under the first request. The cost
	/// of that choice is intended — an engine that cannot prepare fails the deployment here, not by
	/// quietly serving nothing.
	///
	/// Routes marked <see cref="RenderMode.Client"/> are not mapped. Whatever already serves the
	/// application shell — static assets, a fallback view — keeps serving them, and the engine is
	/// never invoked on their behalf.
	/// </remarks>
	/// <param name="endpoints"></param>
	/// <param name="options"></param>
	/// <param name="cancellationToken"></param>
	public static async Task<IReadOnlyList<RenderRoute>> MapRenderEngineAsync(this IEndpointRouteBuilder endpoints, MapRenderEngineOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		options ??= new MapRenderEngineOptions();

		var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Alethic.AspNetCore.EcmaScript");
		var engine = options.ServiceKey is null
			? endpoints.ServiceProvider.GetRequiredService<IRenderEngine>()
			: endpoints.ServiceProvider.GetRequiredKeyedService<IRenderEngine>(options.ServiceKey);

		await engine.PrepareAsync(cancellationToken);

		var routes = await ReadManifestAsync(engine, options, logger, cancellationToken);

		foreach (var route in routes)
		{
			if (route.Pattern is null)
			{
				// Declared but untranslatable: the fallback serves it, and the application's own
				// router still renders the right thing. Only the per-route policy is lost.
				logger.LogDebug("Render route {Id} has no pattern and is left to the fallback.", route.Id ?? "(unnamed)");
				continue;
			}

			if (route.RenderMode == RenderMode.Client)
				continue;

			var endpoint = endpoints.Map(route.Pattern, context => DispatchAsync(context, engine));
			endpoint.WithMetadata(route);
			options.ConfigureEndpoint?.Invoke(route, endpoint);
		}

		if (options.FallbackPattern is not null)
		{
			// Ordered behind everything else so it only answers what nothing more specific claimed,
			// the same way a SPA fallback file does.
			var fallback = endpoints.MapFallback(options.FallbackPattern, context => DispatchAsync(context, engine));
			options.ConfigureEndpoint?.Invoke(null, fallback);
		}

		return routes;
	}

	/// <summary>
	/// Asks the engine for its route manifest, tolerating its absence unless told otherwise.
	/// </summary>
	/// <param name="engine"></param>
	/// <param name="options"></param>
	/// <param name="logger"></param>
	/// <param name="cancellationToken"></param>
	/// <exception cref="InvalidOperationException"></exception>
	static async Task<IReadOnlyList<RenderRoute>> ReadManifestAsync(IRenderEngine engine, MapRenderEngineOptions options, ILogger logger, CancellationToken cancellationToken)
	{
		IReadOnlyList<RenderRoute>? routes;

		try
		{
			routes = await engine.GetRoutesAsync(cancellationToken);
		}
		catch (Exception e)
		{
			if (options.RequireManifest)
				throw new InvalidOperationException("The rendering engine offers no route manifest, and one is required.", e);

			logger.LogInformation("The rendering engine offers no route manifest; serving it entirely from the fallback. ({Reason})", e.Message);
			return [];
		}

		if (routes is null && options.RequireManifest)
			throw new InvalidOperationException("The rendering engine offers no route manifest, and one is required.");

		return routes ?? [];
	}

	/// <summary>
	/// Serves one HTTP request from the engine.
	/// </summary>
	/// <remarks>
	/// The response is copied as it is produced, so a page that emits its shell ahead of suspended
	/// content reaches the client that way rather than all at once. The client going away aborts the
	/// render through the request's cancellation, not merely the copy.
	/// </remarks>
	/// <param name="context"></param>
	/// <param name="engine"></param>
	static async Task DispatchAsync(HttpContext context, IRenderEngine engine)
	{
		using var request = BuildRequest(context);
		using var response = await engine.SendAsync(request, context.RequestAborted);

		context.Response.StatusCode = (int)response.StatusCode;

		foreach (var header in response.Headers)
			context.Response.Headers[header.Key] = header.Value.ToArray();

		foreach (var header in response.Content.Headers)
		{
			// The body arrives as a stream of unknown length, and the server frames it itself; a
			// length or transfer coding copied from the engine would claim otherwise.
			if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
				continue;
			if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
				continue;

			context.Response.Headers[header.Key] = header.Value.ToArray();
		}

		await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);

		// Copied chunk by chunk with a flush per chunk, so progress made by the render is progress
		// seen by the client. A plain copy would batch on the server's own buffer instead.
		var buffer = new byte[16 * 1024];
		while (true)
		{
			var read = await body.ReadAsync(buffer, context.RequestAborted);
			if (read == 0)
				break;

			await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
			await context.Response.Body.FlushAsync(context.RequestAborted);
		}
	}

	/// <summary>
	/// Rebuilds the incoming request in the shape the engine expects.
	/// </summary>
	/// <param name="context"></param>
	static HttpRequestMessage BuildRequest(HttpContext context)
	{
		var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.GetEncodedUrl());

		if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
			request.Content = new StreamContent(context.Request.Body);

		foreach (var header in context.Request.Headers)
		{
			// The authority travels in the URL; a Host header besides it is at best redundant and at
			// worst contradicts a PathBase-adjusted address.
			if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
				continue;

			if (request.Headers.TryAddWithoutValidation(header.Key, (string?[])header.Value) == false)
				request.Content?.Headers.TryAddWithoutValidation(header.Key, (string?[])header.Value);
		}

		return request;
	}

}
