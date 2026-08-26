using Microsoft.JavaScript.NodeApi;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Reading a CommonJS module's exports.
/// </summary>
public static class NodeModuleExports
{

    /// <summary>
    /// Resolves a module's default export: the <c>default</c> property where one exists, and the
    /// exports object itself where the bundler assigned it directly.
    /// </summary>
    /// <remarks>
    /// Bundlers disagree — some set <c>module.exports</c> outright, others hang a <c>default</c>
    /// property off it — and both shapes have to serve. Centralized here so a request handler and a route
    /// provider reading the same bundle agree on what its default export is.
    ///
    /// Must be called on the engine's thread.
    /// </remarks>
    /// <param name="exports"></param>
    public static JSValue Default(JSValue exports)
    {
        var value = exports["default"];
        return value.IsNullOrUndefined() ? exports : value;
    }

}
