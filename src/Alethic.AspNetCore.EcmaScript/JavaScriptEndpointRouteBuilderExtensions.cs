using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Alethic.AspNetCore.EcmaScript;
using Alethic.EcmaScript.Hosting;
using Alethic.EcmaScript.Hosting.Http;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Mounts JavaScript applications into the endpoint table.
/// </summary>
public static class JavaScriptEndpointRouteBuilderExtensions
{

	/// <summary>
	/// Maps a single pattern to a JavaScript application's fetch export.
	/// </summary>
	/// <param name="endpoints"></param>
	/// <param name="pattern"></param>
	/// <param name="module"></param>
	/// <param name="poolName"></param>
	public static IEndpointConventionBuilder MapJavaScript(this IEndpointRouteBuilder endpoints, string pattern, JavaScriptModuleSource module, string poolName = "Default")
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		ArgumentNullException.ThrowIfNull(pattern);
		ArgumentNullException.ThrowIfNull(module);

		var application = endpoints.ServiceProvider
			.GetRequiredService<IJavaScriptEnginePoolProvider>()
			.Get(poolName)
			.GetHttpApplication(module);

		return endpoints.Map(pattern, context => JavaScriptRequestDispatcher.DispatchAsync(context, application));
	}

	/// <summary>
	/// Mounts a JavaScript application: warms the pool, reads the application's route manifest, and
	/// maps an endpoint per route plus a fallback.
	/// </summary>
	/// <remarks>
	/// Awaited before the server starts, deliberately: the warmed engine is what answers the manifest
	/// query, so the route table is the running router's rather than a copy that could drift, and the
	/// evaluation stall lands at startup instead of under the first request. The cost of that choice
	/// is real and intended — a module that fails to evaluate fails the deployment here, not by
	/// quietly serving nothing.
	///
	/// Routes marked <see cref="RenderMode.Client"/> are not mapped. Whatever already serves the
	/// application shell — static assets, a fallback view — keeps serving them, and the engine is
	/// never touched on their behalf.
	/// </remarks>
	/// <param name="endpoints"></param>
	/// <param name="options"></param>
	/// <param name="cancellationToken"></param>
	public static async Task<IReadOnlyList<JavaScriptRoute>> MapJavaScriptApplicationAsync(this IEndpointRouteBuilder endpoints, JavaScriptApplicationOptions options, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		ArgumentNullException.ThrowIfNull(options);

		var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Alethic.AspNetCore.EcmaScript");
		var pool = endpoints.ServiceProvider.GetRequiredService<IJavaScriptEnginePoolProvider>().Get(options.PoolName);

		// Warm the decorated module — the one requests will actually run — so the first request pays
		// for nothing the startup could have.
		await pool.WarmAsync(options.Module.AsHttpModule(), cancellationToken);
		var application = pool.GetHttpApplication(options.Module);

		var routes = await ReadManifestAsync(application, options, logger, cancellationToken);

		foreach (var route in routes)
		{
			if (route.Pattern is null)
			{
				// Declared but untranslatable: the fallback serves it, and the application's own
				// router still renders the right thing. Only the per-route policy is lost.
				logger.LogDebug("JavaScript route {Id} has no pattern and is left to the fallback.", route.Id ?? "(unnamed)");
				continue;
			}

			if (route.RenderMode == RenderMode.Client)
				continue;

			var endpoint = endpoints.Map(route.Pattern, context => JavaScriptRequestDispatcher.DispatchAsync(context, application));
			endpoint.WithMetadata(route);
			options.ConfigureEndpoint?.Invoke(route, endpoint);
		}

		if (options.FallbackPattern is not null)
		{
			// Ordered behind everything else so it only answers what nothing more specific claimed,
			// the same way a SPA fallback file does.
			var fallback = endpoints.MapFallback(options.FallbackPattern, context => JavaScriptRequestDispatcher.DispatchAsync(context, application));
			options.ConfigureEndpoint?.Invoke(null, fallback);
		}

		return routes;
	}

	/// <summary>
	/// Asks the module for its route manifest, tolerating its absence unless told otherwise.
	/// </summary>
	/// <param name="application"></param>
	/// <param name="options"></param>
	/// <param name="logger"></param>
	/// <param name="cancellationToken"></param>
	/// <exception cref="InvalidOperationException"></exception>
	static async Task<IReadOnlyList<JavaScriptRoute>> ReadManifestAsync(IJavaScriptHttpApplication application, JavaScriptApplicationOptions options, ILogger logger, CancellationToken cancellationToken)
	{
		try
		{
			var json = await application.GetRoutesJsonAsync(options.RoutesExport, cancellationToken);
			if (json is not null)
				return JsonSerializer.Deserialize<List<JavaScriptRoute>>(json) ?? [];
		}
		catch (Exception e)
		{
			if (options.RequireManifest)
				throw new InvalidOperationException($"JavaScript module '{application.Source.Name}' offers no {options.RoutesExport}() manifest, and one is required.", e);

			logger.LogInformation("JavaScript module {Module} offers no {Export}() manifest; serving it entirely from the fallback. ({Reason})",
				application.Source.Name, options.RoutesExport, e.Message);
			return [];
		}

		if (options.RequireManifest)
			throw new InvalidOperationException($"JavaScript module '{application.Source.Name}' returned no route manifest from {options.RoutesExport}().");

		return [];
	}

}
