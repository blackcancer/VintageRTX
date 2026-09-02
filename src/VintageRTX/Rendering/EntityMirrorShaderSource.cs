using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>Loads the asset-backed point projection used for finite entity reflections on liquids.</summary>
internal static class EntityMirrorShaderSource
{
    /// <summary>Canonical vertex shader that identifies and mirrors late opaque entity pixels.</summary>
    internal const string VertexAssetCode = "vintagertx:shaders/entitymirror.vert";

    /// <summary>Canonical fragment shader that preserves the captured entity radiance and coverage.</summary>
    internal const string FragmentAssetCode = "vintagertx:shaders/entitymirror.frag";

    /// <summary>Loads and validates both GLSL 3.30 shader assets.</summary>
    /// <param name="assets">Active Vintage Story asset manager.</param>
    /// <returns>Validated vertex and fragment source.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assets"/> is null.</exception>
    /// <exception cref="FileNotFoundException">One canonical asset is missing.</exception>
    /// <exception cref="InvalidDataException">One source does not target GLSL 3.30.</exception>
    internal static DisplayShaderProgramSource Load(IAssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        string vertex = Read(assets, VertexAssetCode, "vertex");
        string fragment = Read(assets, FragmentAssetCode, "fragment");
        return new DisplayShaderProgramSource(vertex, fragment);
    }

    /// <summary>Reads one required shader and enforces the renderer's GLSL version contract.</summary>
    /// <param name="assets">Active asset manager.</param>
    /// <param name="assetCode">Canonical asset code.</param>
    /// <param name="stage">Human-readable shader stage.</param>
    /// <returns>Validated source text.</returns>
    private static string Read(IAssetManager assets, string assetCode, string stage)
    {
        string source = assets.TryGet(new AssetLocation(assetCode))?.ToText()
            ?? throw new FileNotFoundException(
                $"VintageRTX entity-mirror {stage} shader asset '{assetCode}' is missing.");
        if (!source.StartsWith("#version 330 core", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"VintageRTX entity-mirror {stage} shader '{assetCode}' must begin with '#version 330 core'.");
        }

        return source;
    }
}
