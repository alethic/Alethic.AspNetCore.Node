using System;

namespace Alethic.AspNetCore.EcmaScript.Node;

/// <summary>
/// Configures a rendering engine running on a Node engine pool.
/// </summary>
public class NodeRenderEngineOptions
{

	/// <summary>
	/// The application's server module: a self-contained CommonJS bundle whose default export
	/// carries <c>fetch(request)</c> and, optionally, the route manifest export.
	/// </summary>
	public NodeModuleSource? Module { get; set; }

	/// <summary>
	/// Service key of the pool to render on, for applications running more than one. Null resolves
	/// the unkeyed registration.
	/// </summary>
	public object? PoolKey { get; set; }

	/// <summary>
	/// Name of the application's route-manifest export. Defaults to <c>routes</c>.
	/// </summary>
	public string RoutesExport { get; set; } = "routes";

}
