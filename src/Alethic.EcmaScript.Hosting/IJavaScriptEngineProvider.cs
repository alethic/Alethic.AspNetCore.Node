using System.Threading;
using System.Threading.Tasks;

namespace Alethic.EcmaScript.Hosting;

/// <summary>
/// Creates engines of a particular kind. This is the seam alternative runtimes plug into.
/// </summary>
public interface IJavaScriptEngineProvider
{

	/// <summary>
	/// Creates and starts a new engine.
	/// </summary>
	/// <param name="options"></param>
	/// <param name="cancellationToken"></param>
	ValueTask<IJavaScriptEngine> CreateAsync(JavaScriptEnginePoolOptions options, CancellationToken cancellationToken);

}
