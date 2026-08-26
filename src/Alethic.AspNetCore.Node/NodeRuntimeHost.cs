using System;

using Microsoft.JavaScript.NodeApi.Runtime;

namespace Alethic.AspNetCore.Node;

/// <summary>
/// Owns the one embedding platform a process is allowed.
/// </summary>
/// <remarks>
/// Node permits a single embedding platform per process, so this is a static gate rather than a
/// registered service: two pools in one application must share it, and a second platform would fail
/// at native level rather than raise anything a caller could handle.
/// </remarks>
static class NodeRuntimeHost
{

    static readonly object sync = new();

    static NodeEmbeddingPlatform? platform;
    static string? loadedFrom;

    /// <summary>
    /// Returns the process-wide platform, creating it from the given library on first use.
    /// </summary>
    /// <param name="libNodePath"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public static NodeEmbeddingPlatform GetOrCreate(string libNodePath)
    {
        if (platform is not null)
            return Verify(libNodePath);

        lock (sync)
        {
            if (platform is not null)
                return Verify(libNodePath);

            platform = new NodeEmbeddingPlatform(new NodeEmbeddingPlatformSettings() { LibNodePath = libNodePath });
            loadedFrom = libNodePath;
            return platform;
        }
    }

    /// <summary>
    /// Confirms a second caller is asking for the library already loaded.
    /// </summary>
    /// <param name="libNodePath"></param>
    /// <exception cref="InvalidOperationException"></exception>
    static NodeEmbeddingPlatform Verify(string libNodePath)
    {
        if (string.Equals(loadedFrom, libNodePath, StringComparison.OrdinalIgnoreCase) == false)
            throw new InvalidOperationException(
                $"A Node runtime is already loaded from '{loadedFrom}'. Only one may be loaded per process, so '{libNodePath}' cannot also be used.");

        return platform!;
    }

}
