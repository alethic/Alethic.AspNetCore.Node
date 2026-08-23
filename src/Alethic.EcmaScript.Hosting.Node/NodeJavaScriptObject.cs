using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.JavaScript.NodeApi;

namespace Alethic.EcmaScript.Hosting.Node;

/// <summary>
/// A handle to an object inside an embedded Node engine.
/// </summary>
/// <remarks>
/// The <see cref="JSReference"/> is what makes this possible: a raw <see cref="JSValue"/> is valid
/// only inside the scope that produced it, and awaiting ends that scope even without leaving the
/// thread, so anything held across calls is held by reference and re-read inside each operation.
/// Every operation marshals onto the engine's thread and converts its result before returning —
/// primitives by value, structure as further handles.
/// </remarks>
class NodeJavaScriptObject : IJavaScriptObject
{

	readonly NodeEngine engine;
	readonly JSReference reference;

	int disposed;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="engine"></param>
	/// <param name="reference"></param>
	public NodeJavaScriptObject(NodeEngine engine, JSReference reference)
	{
		this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
		this.reference = reference ?? throw new ArgumentNullException(nameof(reference));
	}

	/// <inheritdoc />
	public IJavaScriptEngine Engine => engine;

	/// <summary>
	/// The engine, in its concrete form.
	/// </summary>
	internal NodeEngine NodeEngine => engine;

	/// <summary>
	/// Dereferences the handle. Must be called on the engine's thread.
	/// </summary>
	internal JSValue Value => reference.GetValue();

	/// <inheritdoc />
	public Task<JavaScriptValue> GetAsync(string name, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		ThrowIfDisposed();

		return engine.Runtime.RunAsync(() => Task.FromResult(engine.Convert(reference.GetValue()[name])));
	}

	/// <inheritdoc />
	public Task<JavaScriptValue> GetAsync(int index, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		return engine.Runtime.RunAsync(() => Task.FromResult(engine.Convert(reference.GetValue()[index])));
	}

	/// <inheritdoc />
	public Task SetAsync(string name, JavaScriptValue value, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		ThrowIfDisposed();
		ThrowIfForeign(value);

		return engine.Runtime.RunAsync(() =>
		{
			reference.GetValue().SetProperty(name, engine.ConvertBack(value));
			return Task.FromResult(0);
		});
	}

	/// <inheritdoc />
	public Task<JavaScriptValue> InvokeAsync(string name, JavaScriptValue[] arguments, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(arguments);
		ThrowIfDisposed();
		ThrowIfForeign(arguments);

		return engine.Runtime.RunAsync(() =>
		{
			var target = reference.GetValue();
			var function = target[name];
			if (function.IsFunction() == false)
				throw new InvalidOperationException($"The object has no function named '{name}'.");

			return Task.FromResult(engine.Convert(function.Call(target, engine.ConvertBack(arguments))));
		});
	}

	/// <inheritdoc />
	public Task<JavaScriptValue> CallAsync(JavaScriptValue[] arguments, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ThrowIfDisposed();
		ThrowIfForeign(arguments);

		return engine.Runtime.RunAsync(() =>
		{
			var function = reference.GetValue();
			if (function.IsFunction() == false)
				throw new InvalidOperationException("The object is not a function.");

			return Task.FromResult(engine.Convert(function.Call(JSValue.Undefined, engine.ConvertBack(arguments))));
		});
	}

	/// <inheritdoc />
	public Task<JavaScriptValue> AwaitAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		return engine.Runtime.RunAsync(async () =>
		{
			// Promise.resolve is exactly await's own treatment: a promise settles, a thenable is
			// followed, and any other value passes through unchanged.
			var promise = (JSPromise)JSValue.Global["Promise"].CallMethod("resolve", reference.GetValue());
			return engine.Convert(await promise.AsTask());
		});
	}

	/// <inheritdoc />
	public Task<byte[]> ToByteArrayAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		return engine.Runtime.RunAsync(() =>
		{
			var value = reference.GetValue();

			if (value.IsTypedArray())
				return Task.FromResult(((JSTypedArray<byte>)value).Span.ToArray());

			if (value.IsArrayBuffer())
				return Task.FromResult(value.GetArrayBufferInfo().ToArray());

			throw new InvalidOperationException("The object is neither a typed array nor an array buffer.");
		});
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return ValueTask.CompletedTask;

		// The reference must die on the engine's thread; posting is fire-and-forget because there is
		// nothing to wait for and nothing a caller could do about a failure. A disposed engine has
		// already torn the whole world down, reference included.
		engine.TryPost(reference.Dispose);
		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Guards operations against use after disposal.
	/// </summary>
	void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

	/// <summary>
	/// Rejects handles minted by another engine, on the calling thread, before anything marshals.
	/// </summary>
	/// <remarks>
	/// Affinity is a .NET-side fact, so checking it here surfaces the violation as itself rather
	/// than wrapped in the engine's own exception from the far side of a post.
	/// </remarks>
	/// <param name="value"></param>
	/// <exception cref="InvalidOperationException"></exception>
	void ThrowIfForeign(JavaScriptValue value)
	{
		if (value.Kind == JavaScriptValueKind.Object &&
			(value.AsObject() is not NodeJavaScriptObject handle || ReferenceEquals(handle.NodeEngine, engine) == false))
			throw new InvalidOperationException("The object handle belongs to a different engine and cannot be passed to this one.");
	}

	/// <summary>
	/// Rejects foreign handles anywhere in an argument list.
	/// </summary>
	/// <param name="values"></param>
	void ThrowIfForeign(JavaScriptValue[] values)
	{
		foreach (var value in values)
			ThrowIfForeign(value);
	}

}
