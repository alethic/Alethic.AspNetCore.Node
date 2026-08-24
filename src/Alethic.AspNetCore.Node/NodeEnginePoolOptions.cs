using System;

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
