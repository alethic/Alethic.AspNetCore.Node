using System.Text.Json.Serialization;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// One entry of a JavaScript application's route manifest.
/// </summary>
/// <remarks>
/// The manifest comes from the application's own router, so nothing is declared twice; the entry
/// module translates its framework's pattern syntax into ASP.NET's, being the one place that knows
/// which framework it is. A pattern the entry cannot express is null, and such a route is simply
/// served by the application's fallback rather than by a mapped endpoint.
/// </remarks>
public sealed record JavaScriptRoute
{

	/// <summary>
	/// Route pattern in ASP.NET template syntax, or null when it does not translate.
	/// </summary>
	[JsonPropertyName("pattern")]
	public string? Pattern { get; init; }

	/// <summary>
	/// How the route expects to be rendered.
	/// </summary>
	[JsonPropertyName("renderMode")]
	[JsonConverter(typeof(JsonStringEnumConverter<RenderMode>))]
	public RenderMode RenderMode { get; init; } = RenderMode.Server;

	/// <summary>
	/// Optional identifier, for diagnostics and for correlating with the application's own routing.
	/// </summary>
	[JsonPropertyName("id")]
	public string? Id { get; init; }

}
