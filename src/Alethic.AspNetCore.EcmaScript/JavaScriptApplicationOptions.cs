using System;

using Alethic.EcmaScript.Hosting;

using Microsoft.AspNetCore.Builder;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// Describes how one JavaScript application is mounted into the endpoint table.
/// </summary>
public class JavaScriptApplicationOptions
{

	/// <summary>
	/// The application's server module.
	/// </summary>
	public required JavaScriptModuleSource Module { get; set; }

	/// <summary>
	/// Name of the engine pool that runs it. Defaults to the default pool.
	/// </summary>
	public string PoolName { get; set; } = "Default";

	/// <summary>
	/// Export that yields the route manifest. Defaults to <c>routes</c>.
	/// </summary>
	public string RoutesExport { get; set; } = "routes";

	/// <summary>
	/// Pattern for the endpoint that answers whatever the manifest did not, and the whole application
	/// when there is no manifest. Null suppresses it, leaving unmatched paths to the rest of the
	/// application. Defaults to a root catch-all.
	/// </summary>
	public string? FallbackPattern { get; set; } = "/{**path}";

	/// <summary>
	/// Fails startup when the module offers no route manifest. Off by default, in which case such an
	/// application is served entirely by the fallback endpoint.
	/// </summary>
	public bool RequireManifest { get; set; }

	/// <summary>
	/// Applies host policy to each mapped route: caching by render mode, authorization by path, and
	/// anything else the endpoint builder can carry. Called once per mapped endpoint, with a null
	/// route for the fallback.
	/// </summary>
	public Action<JavaScriptRoute?, IEndpointConventionBuilder>? ConfigureEndpoint { get; set; }

}
