using Vintagestory.API.Common;

namespace VintageRTX.Rendering;

/// <summary>
/// Loads the canonical display shaders from VintageRTX assets. The GLSL itself
/// deliberately lives outside the assembly so resource packs, diagnostics, the
/// game renderer, and RenderLab all consume the same source files.
/// </summary>
internal static class DisplayShaderSource
{
    /// <summary>Canonical domain/path of the full-screen triangle vertex shader.</summary>
    public const string VertexAssetCode = "vintagertx:shaders/display.vert";
    /// <summary>Canonical domain/path of the physical transport/composition fragment shader.</summary>
    public const string FragmentAssetCode = "vintagertx:shaders/display.frag";

    /// <summary>Loads and validates the production shader pair through Vintage Story's asset origins.</summary>
    /// <param name="assets">Active asset manager, including resource-pack/mod overrides.</param>
    /// <returns>UTF-8 vertex and fragment sources beginning with GLSL 3.30.</returns>
    /// <exception cref="FileNotFoundException">A canonical shader asset is absent.</exception>
    /// <exception cref="InvalidDataException">An asset is unreadable, empty, or targets the wrong GLSL version.</exception>
    public static DisplayShaderProgramSource Load(IAssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return new DisplayShaderProgramSource(
            ReadAsset(assets, new AssetLocation(VertexAssetCode), "vertex"),
            ReadAsset(assets, new AssetLocation(FragmentAssetCode), "fragment"));
    }

    /// <summary>
    /// Loads the same asset layout from a build output or the mod project root.
    /// This is the only non-game path used by RenderLab and deterministic tests.
    /// </summary>
    public static DisplayShaderProgramSource LoadFromFileSystem(string modRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modRoot);
        string shaderDirectory = Path.Combine(
            Path.GetFullPath(modRoot),
            "assets",
            "vintagertx",
            "shaders");
        return new DisplayShaderProgramSource(
            ReadFile(Path.Combine(shaderDirectory, "display.vert"), "vertex"),
            ReadFile(Path.Combine(shaderDirectory, "display.frag"), "fragment"));
    }

    /// <summary>Resolves one asset, decodes text, and applies the shared source precondition.</summary>
    /// <param name="assets">Active asset manager.</param>
    /// <param name="location">Canonical shader asset identity.</param>
    /// <param name="stage">Human-readable vertex/fragment label for diagnostics.</param>
    /// <returns>Validated GLSL source.</returns>
    private static string ReadAsset(
        IAssetManager assets,
        AssetLocation location,
        string stage)
    {
        IAsset? asset = assets.TryGet(location);
        if (asset is null)
        {
            throw new FileNotFoundException(
                $"VintageRTX {stage} display shader asset '{location}' is missing. "
                + "Verify that assets/vintagertx/shaders is present in the installed mod.");
        }

        string source;
        try
        {
            source = asset.ToText();
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"VintageRTX {stage} display shader asset '{location}' could not be decoded as UTF-8 text.",
                exception);
        }

        return RequireSource(source, stage, location.ToString());
    }

    /// <summary>Reads one filesystem shader for RenderLab/tests and applies production validation.</summary>
    /// <param name="path">Absolute or normalized shader file path.</param>
    /// <param name="stage">Human-readable vertex/fragment label.</param>
    /// <returns>Validated GLSL source.</returns>
    private static string ReadFile(string path, string stage)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"VintageRTX {stage} display shader file is missing at '{path}'. "
                + "Verify the project content-copy contract.",
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
                $"VintageRTX {stage} display shader file '{path}' could not be read.",
                exception);
        }

        return RequireSource(source, stage, path);
    }

    /// <summary>Rejects whitespace or a source whose first token is not the required GLSL 3.30 directive.</summary>
    /// <param name="source">Decoded shader text.</param>
    /// <param name="stage">Human-readable shader stage.</param>
    /// <param name="identity">Asset code or filesystem path used in errors.</param>
    /// <returns>The unchanged validated source.</returns>
    private static string RequireSource(string source, string stage, string identity)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidDataException(
                $"VintageRTX {stage} display shader '{identity}' is empty.");
        }

        if (!source.StartsWith("#version 330 core", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"VintageRTX {stage} display shader '{identity}' must begin with '#version 330 core'.");
        }

        return source;
    }
}

/// <summary>
/// Validated vertex/fragment source pair shared by the game renderer and standalone RenderLab.
/// Both strings target GLSL 3.30 and together define the strict texture/uniform ABI.
/// </summary>
internal readonly record struct DisplayShaderProgramSource(
    string Vertex,
    string Fragment);

/// <summary>Loads the asset-backed single-pass first-person reflection-source isolation shader.</summary>
internal static class ReflectionIsolationShaderSource
{
    /// <summary>Canonical fragment shader kept outside compiled C# code for mod/resource-pack overrides.</summary>
    public const string FragmentAssetCode = "vintagertx:shaders/reflectionsource.frag";

    /// <summary>Loads the shared full-screen vertex shader and the dedicated isolation fragment shader.</summary>
    /// <param name="assets">Active Vintage Story asset manager.</param>
    /// <returns>Validated GLSL 3.30 source pair.</returns>
    internal static DisplayShaderProgramSource Load(IAssetManager assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        string vertex = assets.TryGet(new AssetLocation(DisplayShaderSource.VertexAssetCode))?.ToText()
            ?? throw new FileNotFoundException(
                $"VintageRTX vertex shader asset '{DisplayShaderSource.VertexAssetCode}' is missing.");
        string fragment = assets.TryGet(new AssetLocation(FragmentAssetCode))?.ToText()
            ?? throw new FileNotFoundException(
                $"VintageRTX reflection isolation shader asset '{FragmentAssetCode}' is missing.");
        if (!vertex.StartsWith("#version 330 core", StringComparison.Ordinal)
            || !fragment.StartsWith("#version 330 core", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "VintageRTX reflection isolation shaders must begin with '#version 330 core'.");
        }

        return new DisplayShaderProgramSource(vertex, fragment);
    }
}
