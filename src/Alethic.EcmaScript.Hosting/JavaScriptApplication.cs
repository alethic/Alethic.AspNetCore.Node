using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Binds a module to a pool, so callers name what they want run without naming where it runs.
/// </summary>
sealed class JavaScriptApplication : IJavaScriptApplication
{

	readonly JavaScriptEnginePool pool;
	readonly JavaScriptModuleSource source;

	/// <summary>
	/// Initializes a new instance.
	/// </summary>
	/// <param name="pool"></param>
	/// <param name="source"></param>
	public JavaScriptApplication(JavaScriptEnginePool pool, JavaScriptModuleSource source)
	{
		this.pool = pool ?? throw new ArgumentNullException(nameof(pool));
		this.source = source ?? throw new ArgumentNullException(nameof(source));
	}

	/// <inheritdoc />
	public JavaScriptModuleSource Source => source;

	/// <inheritdoc />
	public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		// The slot outlives this method deliberately. A streaming response is still being rendered
		// when it is returned, so its engine stays charged until the caller disposes the response.
		var slot = await pool.AcquireAsync(source, cancellationToken);

		try
		{
			var response = await slot.Instance.SendAsync(request, cancellationToken);
			response.Content = new ReleasingHttpContent(response.Content, slot);
			return response;
		}
		catch
		{
			await slot.DisposeAsync();
			throw;
		}
	}

	/// <inheritdoc />
	public Task<T?> InvokeAsync<T>(string export, object?[] arguments, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(export);
		ArgumentNullException.ThrowIfNull(arguments);

		return pool.InvokeAsync(source, i => i.InvokeAsync<T>(export, arguments, cancellationToken), cancellationToken);
	}

}
