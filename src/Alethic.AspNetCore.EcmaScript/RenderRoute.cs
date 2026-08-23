using System.Text.Json.Serialization;

namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// One entry of a rendering engine's route manifest.
/// </summary>
/// <remarks>
/// A pattern the application could not express in ASP.NET template syntax is null; such a route is
/// served by the fallback endpoint rather than a mapped one, losing only its per-route policy.
/// </remarks>
public sealed record RenderRoute
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
