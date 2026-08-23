using System.Text.Json.Serialization;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// One entry of a rendering engine's route manifest.
/// </summary>
/// <remarks>
/// Patterns are URLPattern pathnames — the WHATWG syntax every framework's routes lower to — so an
/// application declares its manifest without knowing anything about its host. A null pattern, or one
/// using URLPattern features the host cannot express, is served by the fallback endpoint rather than
/// a mapped one, losing only its per-route policy.
/// </remarks>
public sealed record RenderRoute
{

	/// <summary>
	/// Route pattern as a URLPattern pathname, <c>/parks/:parkRef</c> for example, or null for a
	/// route that has no expressible pattern.
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
