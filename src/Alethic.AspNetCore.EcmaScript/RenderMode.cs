namespace Alethic.AspNetCore.EcmaScript;

/// <summary>
/// How a route in a JavaScript application's manifest expects to be rendered.
/// </summary>
public enum RenderMode
{

	/// <summary>
	/// The client renders this route; the server only supplies the application shell. Requests never
	/// reach an engine, which is what keeps rendering cost on the routes that need it.
	/// </summary>
	Client,

	/// <summary>
	/// The server renders this route per request.
	/// </summary>
	Server,

	/// <summary>
	/// The route's content is stable enough to render once and cache. Served like
	/// <see cref="Server"/>; the distinction exists so the host can attach caching or a build step
	/// can render ahead of time.
	/// </summary>
	Prerender,

}
