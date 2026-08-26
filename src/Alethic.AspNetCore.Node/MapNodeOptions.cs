using System;

using Microsoft.AspNetCore.Builder;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Describes how a request handler is mounted into the endpoint table.
/// </summary>
public class MapNodeOptions
{

    /// <summary>
    /// Pattern for the endpoint that answers whatever no route claimed, and the whole application
    /// where the engine enumerates no routes at all. Null suppresses it, leaving unmatched paths to
    /// the rest of the application. Defaults to a root catch-all.
    /// </summary>
    public string? FallbackPattern { get; set; } = "/{**path}";

    /// <summary>
    /// Applies host policy to each mapped route: caching by render mode, authorization by path, and
    /// anything else the endpoint builder can carry. Called once per mapped endpoint, with a null
    /// route for the fallback.
    /// </summary>
    public Action<RenderRoute?, IEndpointConventionBuilder>? ConfigureEndpoint { get; set; }

}
