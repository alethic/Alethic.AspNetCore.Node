namespace Alethic.AspNetCore.Node;

/// <summary>
/// How a body crosses between the host and the application.
/// </summary>
public enum BodyMode
{

    /// <summary>
    /// Carried in pieces, as the far side asks for them.
    /// </summary>
    /// <remarks>
    /// What is in memory at once is a chunk rather than a body, so neither a large upload nor a large
    /// page is held whole. On the way out it also means progress the render makes is progress the
    /// client sees, which is what lets a shell reach a browser ahead of the content suspended behind
    /// it.
    /// </remarks>
    Streamed,

    /// <summary>
    /// Collected whole before it is handed on.
    /// </summary>
    /// <remarks>
    /// On the way in, a body that has been read once can be read again — a stream cannot, so an
    /// application that clones a request or retries a parse needs this.
    ///
    /// On the way out it buys something the streamed form cannot: nothing is written until the render
    /// has finished, so a failure partway through is still a failure. Streamed, the status has
    /// already gone out by then and a fault can only truncate the page; buffered, it becomes an
    /// ordinary error the host can answer properly. The length is known too, so the response carries
    /// one rather than being framed as chunked. The cost is the whole page in memory, and nothing
    /// reaching the client until all of it exists — which for a render that waits on all its data
    /// before answering was already true.
    /// </remarks>
    Buffered,

}
