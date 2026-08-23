using System;

using Microsoft.AspNetCore.Builder;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// Describes how a rendering engine is mounted into the endpoint table.
/// </summary>
public class MapRenderEngineOptions
{

	/// <summary>
	/// Service key of the engine to mount, for applications running more than one. Null resolves the
	/// unkeyed registration.
	/// </summary>
	public object? ServiceKey { get; set; }

	/// <summary>
	/// Pattern for the endpoint that answers whatever the manifest did not, and the whole application
	/// when there is no manifest. Null suppresses it, leaving unmatched paths to the rest of the
	/// application. Defaults to a root catch-all.
	/// </summary>
	public string? FallbackPattern { get; set; } = "/{**path}";

	/// <summary>
	/// Fails startup when the engine offers no route manifest. Off by default, in which case such an
	/// application is served entirely by the fallback endpoint.
	/// </summary>
	public bool RequireManifest { get; set; }

	/// <summary>
	/// Applies host policy to each mapped route: caching by render mode, authorization by path, and
	/// anything else the endpoint builder can carry. Called once per mapped endpoint, with a null
	/// route for the fallback.
	/// </summary>
	public Action<RenderRoute?, IEndpointConventionBuilder>? ConfigureEndpoint { get; set; }

}
