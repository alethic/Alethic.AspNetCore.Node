using System;
using System.Threading.Tasks;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Configures one pool of embedded Node engines.
/// </summary>
public class NodeEnginePoolOptions
{

    int engineCount = 1;
    int maxConcurrencyPerEngine = 4;

    /// <summary>
    /// Number of engines to run. Defaults to one.
    /// </summary>
    /// <remarks>
    /// This must track the CPU the process is actually entitled to, and deliberately has no derived
    /// default: the processor count reports the host's cores rather than a container's quota, so
    /// deriving one misleads badly under orchestration. Spare CPU with too few engines goes unused,
    /// and engines beyond the available CPU only contend with each other.
    /// </remarks>
    public int EngineCount
    {
        get => engineCount;
        set => engineCount = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Engine count must be greater than zero.");
    }

    /// <summary>
    /// Number of leases that may be held against one engine at a time. Defaults to four.
    /// </summary>
    /// <remarks>
    /// This is backpressure, not mutual exclusion. An engine overlaps many concurrent calls, since
    /// everything awaited inside it yields to its event loop; the gain flattens once concurrency
    /// covers the time spent waiting, and leaving it unbounded merely lets a slow dependency pile up
    /// work until memory runs out.
    /// </remarks>
    public int MaxConcurrencyPerEngine
    {
        get => maxConcurrencyPerEngine;
        set => maxConcurrencyPerEngine = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Concurrency must be greater than zero.");
    }

    /// <summary>
    /// How long an acquisition may wait for capacity before it is abandoned. Defaults to ten seconds.
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs once against each engine as it starts, before anything else is given it.
    /// </summary>
    /// <remarks>
    /// Whatever an engine needs to be true before it answers anything: a global installed, a module
    /// warmed, a polyfill applied. It is handed a lease, which is the ordinary way to be on an
    /// engine's thread — inside it you are writing node-api-dotnet against that runtime, exactly as
    /// you would against a lease taken from the pool.
    ///
    /// Per engine, not per acquisition, and the engine does not join the pool until this returns. A
    /// throw fails the engine and disposes it rather than publishing one whose setup did not
    /// complete, on the same reasoning that a handler which cannot prepare fails the deployment: an
    /// engine that could not be configured is broken, and serving from it is worse than not having
    /// it.
    ///
    /// The provider comes with it because engines are allocated lazily, over the life of the
    /// process: one may stand up long after this was configured, and it should be configured against
    /// the container as it is then rather than against whatever was resolved and captured earlier.
    /// It is the root, engines being singletons — anything scoped is the delegate's own to scope.
    /// </remarks>
    public Func<IServiceProvider, NodeEngineLease, Task>? ConfigureEngine { get; set; }

    /// <summary>
    /// Path to the native Node library, when it cannot be located beside the application or under its
    /// runtime identifier.
    /// </summary>
    public string? LibNodePath { get; set; }

    /// <summary>
    /// Root for Node's package resolution. Defaults to the application's base directory.
    /// </summary>
    /// <remarks>
    /// A module loaded by absolute path does not need this, and a self-contained bundle resolves
    /// nothing outward, so it matters only where a module reaches a <c>node_modules</c> directory.
    /// Node looks here and in parent directories, as it would for any program rooted at this path.
    /// </remarks>
    public string? BaseDirectory { get; set; }

}
