using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>
/// Contains deterministic regression checks for display Shader Asset.
/// </summary>
[TestClass]
public sealed class DisplayShaderAssetTests
{
    /// <summary>
    /// Verifies the build Output Loads The Canonical Shader Files regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void BuildOutputLoadsTheCanonicalShaderFiles()
    {
        DisplayShaderProgramSource source = DisplayShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory);
        StringAssert.StartsWith(source.Vertex, "#version 330 core");
        StringAssert.StartsWith(source.Fragment, "#version 330 core");
        StringAssert.Contains(source.Fragment, "exactDielectricFresnel");
        Assert.IsTrue(source.Fragment.Length > 100_000);

        string implementation = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "Rendering", "DisplayShaderSource.cs"));
        Assert.IsFalse(implementation.Contains("public const string Vertex =", StringComparison.Ordinal));
        Assert.IsFalse(implementation.Contains("public const string Fragment =", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that display gamut compression preserves hue at both the
    /// upper and lower RGB boundaries and that foliage receives no ad-hoc
    /// environment-energy multiplier.
    /// </summary>
    [TestMethod]
    public void FilmicGamutCompressionIsBilateralAndVegetationEnergyIsNeutral()
    {
        string fragment = DisplayShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory).Fragment;

        StringAssert.Contains(fragment, "float negativeChromaPeak = maximumComponent(-chroma)");
        StringAssert.Contains(fragment, "mappedLuminance / negativeChromaPeak");
        StringAssert.Contains(fragment, "min(upperGamutCompression, lowerGamutCompression)");
        Assert.IsFalse(fragment.Contains("vegetationSurface * 0.85", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies static crossed-plane plants retain the raster carrier even when the engine does
    /// not set a wind-animation flag for their draw call.
    /// </summary>
    [TestMethod]
    public void VoxelPlantMaterialCompletesTheTerrainVegetationClassifier()
    {
        string fragment = DisplayShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory).Fragment;

        StringAssert.Contains(
            fragment,
            "int voxelMaterialBits = int(floor(voxelLighting.material.a * 255.0 + 0.5));");
        StringAssert.Contains(
            fragment,
            "float voxelVegetationSurface = (voxelMaterialBits & 8) != 0 ? 1.0 : 0.0;");
        StringAssert.Contains(fragment, "voxelVegetationSurface * (1.0 - dynamicSurface)");
    }

    /// <summary>
    /// Verifies that unresolved microfacet peaks are integrated over a finite
    /// pixel footprint without an empirical post-BRDF energy ceiling, while the
    /// clean reflection behind a first-person overlay remains inspectable.
    /// </summary>
    [TestMethod]
    public void SpecularTransportFiltersFirefliesWithoutErasingReflectionDiagnostics()
    {
        string fragment = DisplayShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory).Fragment;

        StringAssert.Contains(fragment, "float filterSpecularRoughness(");
        StringAssert.Contains(fragment, "float normalFootprintVariance = clamp(");
        StringAssert.Contains(fragment, "authoredVariance + normalFootprintVariance");
        // Numerical GPU tests qualify the real GGX lobe and footprint filter.
        // Do not require the removed empirical radiance clamp to be restored.
        StringAssert.Contains(fragment, "materialFresnel(f0, vh)");
        StringAssert.Contains(fragment, "vec3 directSpecularRadiance = voxelLighting.directSpecular");
        Assert.IsFalse(fragment.Contains("directSpecularRadiance = softLimitSpecularRadiance(",
            StringComparison.Ordinal), "Direct transport must not reintroduce an empirical energy ceiling.");
        StringAssert.Contains(fragment, "coherent world reflection behind a detected first-person overlay");
        Assert.IsFalse(fragment.Contains(
            "if (primaryFirstPersonOverlay > 0.001)\n        {\n            outColor = vec4(0.0",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies the asset Manager Loads The Canonical Vintage Rtx Locations regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AssetManagerLoadsTheCanonicalVintageRtxLocations()
    {
        DisplayShaderProgramSource files = DisplayShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory);
        List<string> requested = [];
        IAsset vertex = TextAsset(files.Vertex);
        IAsset fragment = TextAsset(files.Fragment);
        IAssetManager assets = RuntimeCoverageDispatchProxy.Create<IAssetManager>(
            (method, arguments) =>
            {
                if (method.Name == "TryGet")
                {
                    string location = arguments![0]!.ToString()!;
                    requested.Add(location);
                    return location == DisplayShaderSource.VertexAssetCode ? vertex : fragment;
                }

                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            });

        DisplayShaderProgramSource loaded = DisplayShaderSource.Load(assets);
        Assert.AreEqual(files.Vertex, loaded.Vertex);
        Assert.AreEqual(files.Fragment, loaded.Fragment);
        CollectionAssert.AreEqual(
            new[]
            {
                DisplayShaderSource.VertexAssetCode,
                DisplayShaderSource.FragmentAssetCode
            },
            requested);
    }

    /// <summary>
    /// Verifies the missing And Invalid Shaders Produce Stage Specific Errors regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void MissingAndInvalidShadersProduceStageSpecificErrors()
    {
        string missingRoot = Path.Combine(
            Path.GetTempPath(),
            $"vintagertx-missing-shaders-{Guid.NewGuid():N}");
        FileNotFoundException missing = Assert.ThrowsException<FileNotFoundException>(() =>
            DisplayShaderSource.LoadFromFileSystem(missingRoot));
        StringAssert.Contains(missing.Message, "vertex display shader file is missing");

        IAssetManager missingAssets = RuntimeCoverageDispatchProxy.Create<IAssetManager>(
            (method, _) => method.Name == "TryGet"
                ? null
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        FileNotFoundException missingAsset = Assert.ThrowsException<FileNotFoundException>(() =>
            DisplayShaderSource.Load(missingAssets));
        StringAssert.Contains(missingAsset.Message, DisplayShaderSource.VertexAssetCode);

        IAssetManager emptyAssets = AssetManager(TextAsset("   "), TextAsset("#version 330 core\n"));
        InvalidDataException empty = Assert.ThrowsException<InvalidDataException>(() =>
            DisplayShaderSource.Load(emptyAssets));
        StringAssert.Contains(empty.Message, "vertex display shader");
        StringAssert.Contains(empty.Message, "is empty");

        IAssetManager wrongVersion = AssetManager(TextAsset("void main() {}"), TextAsset("#version 330 core\n"));
        InvalidDataException version = Assert.ThrowsException<InvalidDataException>(() =>
            DisplayShaderSource.Load(wrongVersion));
        StringAssert.Contains(version.Message, "must begin with '#version 330 core'");
    }

    /// <summary>
    /// Executes the asset Manager step used by the deterministic display Shader Asset Tests fixture.
    /// </summary>
    /// <param name="vertex">Coordinate component in the space defined by the tested API.</param>
    /// <param name="fragment">The fragment input used to configure this deterministic test path.</param>
    /// <returns>The asset Manager result consumed by the caller&apos;s assertion.</returns>
    private static IAssetManager AssetManager(IAsset vertex, IAsset fragment)
    {
        return RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) =>
        {
            if (method.Name == "TryGet")
            {
                return arguments![0]!.ToString() == DisplayShaderSource.VertexAssetCode
                    ? vertex
                    : fragment;
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
    }

    /// <summary>
    /// Executes the text Asset step used by the deterministic display Shader Asset Tests fixture.
    /// </summary>
    /// <param name="text">The text input used to configure this deterministic test path.</param>
    /// <returns>The text Asset result consumed by the caller&apos;s assertion.</returns>
    private static IAsset TextAsset(string text)
    {
        return RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "ToText"
                ? text
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
    }
}
