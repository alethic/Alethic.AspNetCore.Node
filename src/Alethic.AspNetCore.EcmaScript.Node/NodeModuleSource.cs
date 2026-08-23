using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.AspNetCore.EcmaScript.Node;

/// <summary>
/// Identifies a JavaScript module and provides its source text.
/// </summary>
/// <remarks>
/// Instances are values: two sources with the same <see cref="Key"/> refer to the same module, which
/// is what lets an engine reuse what it has already evaluated. Modules must be self-contained
/// CommonJS bundles. The embedded runtime has no dynamic import callback registered, so neither an
/// ES module nor an <c>import()</c> inside the bundle can resolve.
/// </remarks>
public abstract class NodeModuleSource : IEquatable<NodeModuleSource>
{

	/// <summary>
	/// Creates a source that reads the module from a file on disk.
	/// </summary>
	/// <param name="path"></param>
	public static NodeModuleSource FromFile(string path) => new FileSource(path);

	/// <summary>
	/// Creates a source over module text already in memory.
	/// </summary>
	/// <param name="name">Name reported in stack traces.</param>
	/// <param name="text"></param>
	public static NodeModuleSource FromText(string name, string text) => new TextSource(name, text);

	/// <summary>
	/// Stable identity of this module. Sources comparing equal are evaluated once per engine.
	/// </summary>
	public abstract string Key { get; }

	/// <summary>
	/// Name reported to the runtime, which is what surfaces in stack traces.
	/// </summary>
	public abstract string Name { get; }

	/// <summary>
	/// Reads the module source text.
	/// </summary>
	/// <param name="cancellationToken"></param>
	public abstract ValueTask<string> ReadAsync(CancellationToken cancellationToken);

	/// <inheritdoc />
	public bool Equals(NodeModuleSource? other) => other is not null && Key == other.Key;

	/// <inheritdoc />
	public override bool Equals(object? obj) => Equals(obj as NodeModuleSource);

	/// <inheritdoc />
	public override int GetHashCode() => Key.GetHashCode();

	/// <inheritdoc />
	public override string ToString() => Name;

	sealed class FileSource : NodeModuleSource
	{

		readonly string path;

		public FileSource(string path)
		{
			this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
		}

		public override string Key => path;

		public override string Name => Path.GetFileName(path);

		public override async ValueTask<string> ReadAsync(CancellationToken cancellationToken) =>
			await File.ReadAllTextAsync(path, cancellationToken);

	}

	sealed class TextSource : NodeModuleSource
	{

		readonly string name;
		readonly string text;

		public TextSource(string name, string text)
		{
			this.name = name ?? throw new ArgumentNullException(nameof(name));
			this.text = text ?? throw new ArgumentNullException(nameof(text));
		}

		public override string Key => "text:" + name;

		public override string Name => name;

		public override ValueTask<string> ReadAsync(CancellationToken cancellationToken) => new(text);

	}

}
