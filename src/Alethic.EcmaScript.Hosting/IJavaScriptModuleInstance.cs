namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// A module that has been evaluated on one particular engine: a handle to its exports object.
/// </summary>
/// <remarks>
/// Nothing more than an <see cref="IJavaScriptObject"/> that knows where it came from. Which exports
/// a module carries, and what shape they take, is its consumers' contract — this layer does not
/// judge it.
/// </remarks>
public interface IJavaScriptModuleInstance : IJavaScriptObject
{

	/// <summary>
	/// The source this instance was evaluated from.
	/// </summary>
	JavaScriptModuleSource Source { get; }

}
