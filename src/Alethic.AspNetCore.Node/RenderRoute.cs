namespace Alethic.AspNetCore.Node;

/// <summary>
/// One route an <see cref="INodeRouteProvider"/> read out of an application.
/// </summary>
/// <remarks>
/// Patterns are URLPattern pathnames — the WHATWG syntax every framework's routes lower to — which
/// is the normalized form a provider translates its framework's own route grammar into. The host
/// then converts the expressible subset to ASP.NET route templates, so each framework is understood
/// in exactly one place and neither end learns the other's syntax. A null pattern, or one using
/// URLPattern features a route template cannot express, is served by the fallback endpoint rather
/// than a mapped one, losing only its per-route policy.
/// </remarks>
public sealed record RenderRoute
{

    /// <summary>
    /// Route pattern as a URLPattern pathname, <c>/parks/:parkRef</c> for example, or null for a
    /// route that has no expressible pattern.
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// How the route expects to be rendered.
    /// </summary>
    public RenderMode RenderMode { get; init; } = RenderMode.Server;

    /// <summary>
    /// Optional identifier, for diagnostics and for correlating with the application's own routing.
    /// </summary>
    public string? Id { get; init; }

}
