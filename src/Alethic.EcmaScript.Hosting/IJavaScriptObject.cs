using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// A handle to an object living inside an engine.
/// </summary>
/// <remarks>
/// The object never leaves the engine; the handle is how .NET operates on it from outside. Every
/// member marshals onto the engine's thread and returns a <see cref="JavaScriptValue"/> — primitives
/// by value, structure as further handles.
///
/// Handles are engine-affine: one may only be passed back to the engine it came from. And they pin
/// what they refer to, so a handle that is done with should be disposed — leaking them is leaking
/// engine memory, invisibly until the engine's footprint says otherwise.
/// </remarks>
public interface IJavaScriptObject : IAsyncDisposable
{

	/// <summary>
	/// The engine this handle belongs to.
	/// </summary>
	IJavaScriptEngine Engine { get; }

	/// <summary>
	/// Reads a property.
	/// </summary>
	/// <param name="name"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptValue> GetAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// Reads an element by index.
	/// </summary>
	/// <param name="index"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptValue> GetAsync(int index, CancellationToken cancellationToken = default);

	/// <summary>
	/// Writes a property.
	/// </summary>
	/// <param name="name"></param>
	/// <param name="value"></param>
	/// <param name="cancellationToken"></param>
	Task SetAsync(string name, JavaScriptValue value, CancellationToken cancellationToken = default);

	/// <summary>
	/// Calls a method on this object, with this object as its <c>this</c>.
	/// </summary>
	/// <param name="name"></param>
	/// <param name="arguments"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptValue> InvokeAsync(string name, JavaScriptValue[] arguments, CancellationToken cancellationToken = default);

	/// <summary>
	/// Calls this object as a function.
	/// </summary>
	/// <param name="arguments"></param>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptValue> CallAsync(JavaScriptValue[] arguments, CancellationToken cancellationToken = default);

	/// <summary>
	/// Awaits this object as a promise and returns what it settles to. A non-promise settles to
	/// itself, the way <c>await</c> treats any value.
	/// </summary>
	/// <param name="cancellationToken"></param>
	Task<JavaScriptValue> AwaitAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Copies this object's bytes out, for typed arrays and array buffers.
	/// </summary>
	/// <param name="cancellationToken"></param>
	Task<byte[]> ToByteArrayAsync(CancellationToken cancellationToken = default);

}
