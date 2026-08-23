using System;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Binds a module to a pool, so callers name what they want run without naming where it runs.
/// </summary>
sealed class JavaScriptPoolModule : IJavaScriptModule
{

	readonly JavaScriptEnginePool pool;
	readonly JavaScriptModuleSource source;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="pool"></param>
	/// <param name="source"></param>
	public JavaScriptPoolModule(JavaScriptEnginePool pool, JavaScriptModuleSource source)
	{
		this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
		this.source = source ?? throw new ArgumentNullException(nameof(source));
	}

	/// <inheritdoc />
	public JavaScriptModuleSource Source => source;

	/// <inheritdoc />
	public Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(export);
		ArgumentNullException.ThrowIfNull(arguments);

		return pool.InvokeAsync(source, i => i.InvokeAsync<T>(export, arguments, cancellationToken), cancellationToken);
	}

	/// <inheritdoc />
	public async Task<JavaScriptStreamResponse> InvokeStreamAsync(string export, object?[] arguments, ReadOnlyMemory<byte>? payload, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(export);
		ArgumentNullException.ThrowIfNull(arguments);

		// The slot outlives this method deliberately: the body is still being produced when the
		// response is returned, so its engine stays charged until the caller disposes it.
		var slot = await pool.AcquireAsync(source, cancellationToken);

		try
		{
			var response = await slot.Instance.InvokeStreamAsync(export, arguments, payload, cancellationToken);
			return response.WithRelease(slot);
		}
		catch
		{
			await slot.DisposeAsync();
			throw;
		}
	}

}
