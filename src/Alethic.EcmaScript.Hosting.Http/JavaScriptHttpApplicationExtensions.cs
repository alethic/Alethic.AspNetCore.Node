using System;

namespace Alethic.EcmaScript.Hosting.Http;

/// <summary>
/// Obtains fetch-contract views over pooled modules.
/// </summary>
public static class JavaScriptHttpApplicationExtensions
{

	/// <summary>
	/// Returns the given module spoken to through the fetch contract.
	/// </summary>
	/// <remarks>
	/// The module's default export must carry <c>fetch(request)</c>. Its source is decorated with the
	/// adapter glue, so the pool sees a distinct module from the undecorated one.
	/// </remarks>
	/// <param name="pool"></param>
	/// <param name="source"></param>
	public static IJavaScriptHttpApplication GetHttpApplication(this IJavaScriptEnginePool pool, JavaScriptModuleSource source)
	{
		ArgumentNullException.ThrowIfNull(pool);
		ArgumentNullException.ThrowIfNull(source);

		return new JavaScriptHttpApplication(pool.GetModule(new HttpModuleSource(source)));
	}

	/// <summary>
	/// Wraps a module source with the fetch adapter, for warming the same module the application will
	/// serve from.
	/// </summary>
	/// <param name="source"></param>
	public static JavaScriptModuleSource AsHttpModule(this JavaScriptModuleSource source)
	{
		ArgumentNullException.ThrowIfNull(source);

		return new HttpModuleSource(source);
	}

}
