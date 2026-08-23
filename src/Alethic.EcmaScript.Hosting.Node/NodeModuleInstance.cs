using System;

using Microsoft.JavaScript.NodeApi;

namespace Alethic.EcmaScript.Hosting.Node;

/// <summary>
/// A module evaluated on one embedded Node engine: a handle to its exports object.
/// </summary>
sealed class NodeModuleInstance : NodeJavaScriptObject, IJavaScriptModuleInstance
{

	readonly JavaScriptModuleSource source;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="engine"></param>
	/// <param name="source"></param>
	/// <param name="exports"></param>
	public NodeModuleInstance(NodeEngine engine, JavaScriptModuleSource source, JSReference exports)
		: base(engine, exports)
	{
		this.source = source ?? throw new ArgumentNullException(nameof(source));
	}

	/// <inheritdoc />
	public JavaScriptModuleSource Source => source;

}
