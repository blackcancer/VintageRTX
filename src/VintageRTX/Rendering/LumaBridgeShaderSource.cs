using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>Loads the asset-backed full-screen shader that restores RGB plus FXAA luma alpha.</summary>
internal static class LumaBridgeShaderSource
{
    /// <summary>Shared full-screen triangle vertex shader.</summary>
    internal const string VertexAssetCode = DisplayShaderSource.VertexAssetCode;
    /// <summary>Dedicated RGB-to-Luma-carrier fragment shader.</summary>
    internal const string FragmentAssetCode = "vintagertx:shaders/lumarepack.frag";

    /// <summary>Loads the canonical bridge shader pair through Vintage Story's asset manager.</summary>
    /// <param name="assets">Active asset manager including mod and resource-pack origins.</param>
    /// <returns>Validated GLSL 3.30 vertex and fragment source.</returns>
    /// <exception cref="FileNotFoundException">A required bridge shader asset is absent.</exception>
    /// <exception cref="InvalidDataException">A shader asset is empty, unreadable, or not GLSL 3.30.</exception>
    internal static DisplayShaderProgramSource Load(IAssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return new DisplayShaderProgramSource(
            ReadAsset(assets, VertexAssetCode, "vertex"),
            ReadAsset(assets, FragmentAssetCode, "fragment"));
    }

    /// <summary>Loads the same canonical shader pair from a mod build output for deterministic tests.</summary>
    /// <param name="modRoot">Build output or mod project root containing the assets directory.</param>
    /// <returns>Validated GLSL 3.30 vertex and fragment source.</returns>
    /// <exception cref="ArgumentException">The supplied root is empty.</exception>
    /// <exception cref="FileNotFoundException">A required bridge shader file is absent.</exception>
    /// <exception cref="InvalidDataException">A shader file is empty or not GLSL 3.30.</exception>
    internal static DisplayShaderProgramSource LoadFromFileSystem(string modRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modRoot);
        string shaderDirectory = Path.Combine(
            Path.GetFullPath(modRoot),
            "assets",
            "vintagertx",
            "shaders");
        return new DisplayShaderProgramSource(
            ReadFile(Path.Combine(shaderDirectory, "display.vert"), "vertex"),
            ReadFile(Path.Combine(shaderDirectory, "lumarepack.frag"), "fragment"));
    }

    /// <summary>Reads and validates one asset-backed shader stage.</summary>
    /// <param name="assets">Active asset manager.</param>
    /// <param name="assetCode">Canonical asset identity.</param>
    /// <param name="stage">Human-readable shader stage.</param>
    /// <returns>Validated shader source.</returns>
    private static string ReadAsset(IAssetManager assets, string assetCode, string stage)
    {
        AssetLocation location = new(assetCode);
        IAsset? asset = assets.TryGet(location);
        if (asset is null)
        {
            throw new FileNotFoundException(
                $"VintageRTX Luma bridge {stage} shader asset '{location}' is missing.");
        }

        string source;
        try
        {
            source = asset.ToText();
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"VintageRTX Luma bridge {stage} shader asset '{location}' is unreadable.",
                exception);
        }

        return RequireSource(source, stage, location.ToString());
    }

    /// <summary>Reads and validates one filesystem-backed shader stage.</summary>
    /// <param name="path">Absolute shader path.</param>
    /// <param name="stage">Human-readable shader stage.</param>
    /// <returns>Validated shader source.</returns>
    private static string ReadFile(string path, string stage)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"VintageRTX Luma bridge {stage} shader file is missing at '{path}'.",
                path);
        }

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"VintageRTX Luma bridge {stage} shader file '{path}' is unreadable.",
                exception);
        }

        return RequireSource(source, stage, path);
    }

    /// <summary>Enforces the canonical non-empty GLSL 3.30 source precondition.</summary>
    /// <param name="source">Decoded shader text.</param>
    /// <param name="stage">Human-readable shader stage.</param>
    /// <param name="identity">Asset code or filesystem path used in diagnostics.</param>
    /// <returns>The unchanged validated source.</returns>
    private static string RequireSource(string source, string stage, string identity)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidDataException(
                $"VintageRTX Luma bridge {stage} shader '{identity}' is empty.");
        }

        if (!source.StartsWith("#version 330 core", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"VintageRTX Luma bridge {stage} shader '{identity}' must begin with '#version 330 core'.");
        }

        return source;
    }
}
