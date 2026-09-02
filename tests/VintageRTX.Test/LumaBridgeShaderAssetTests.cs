using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>Verifies the canonical asset-backed Luma repack shader.</summary>
[TestClass]
public sealed class LumaBridgeShaderAssetTests
{
    /// <summary>Loads the build-output assets and checks the RGB/luma output ABI.</summary>
    [TestMethod]
    public void BuildOutputContainsCanonicalRgbLumaRepackShader()
    {
        DisplayShaderProgramSource source = LumaBridgeShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory);
        StringAssert.StartsWith(source.Vertex, "#version 330 core");
        StringAssert.StartsWith(source.Fragment, "#version 330 core");
        StringAssert.Contains(source.Fragment, "uniform sampler2D sourceColor");
        StringAssert.Contains(source.Fragment, "vec3(0.299, 0.587, 0.114)");
        StringAssert.Contains(source.Fragment, "vec4(color, dot(color, LUMA))");

        string implementation = File.ReadAllText(Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src", "VintageRTX", "Rendering", "LumaRenderBridge.cs"));
        Assert.IsFalse(implementation.Contains("#version", StringComparison.Ordinal));
    }

    /// <summary>Loads both canonical shader locations through an asset-manager double.</summary>
    [TestMethod]
    public void AssetManagerLoadsCanonicalBridgeLocations()
    {
        DisplayShaderProgramSource files = LumaBridgeShaderSource.LoadFromFileSystem(
            AppContext.BaseDirectory);
        List<string> requested = [];
        IAssetManager assets = RuntimeCoverageDispatchProxy.Create<IAssetManager>(
            (method, arguments) =>
            {
                if (method.Name != "TryGet")
                {
                    return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
                }

                string location = arguments![0]!.ToString()!;
                requested.Add(location);
                return TextAsset(location == LumaBridgeShaderSource.VertexAssetCode
                    ? files.Vertex
                    : files.Fragment);
            });

        DisplayShaderProgramSource loaded = LumaBridgeShaderSource.Load(assets);
        Assert.AreEqual(files.Vertex, loaded.Vertex);
        Assert.AreEqual(files.Fragment, loaded.Fragment);
        CollectionAssert.AreEqual(
            new[]
            {
                LumaBridgeShaderSource.VertexAssetCode,
                LumaBridgeShaderSource.FragmentAssetCode
            },
            requested);
    }

    /// <summary>Rejects missing, empty, and incorrectly versioned shader assets.</summary>
    [TestMethod]
    public void InvalidBridgeAssetsProduceSpecificFailures()
    {
        IAssetManager missing = AssetManager(null, null);
        FileNotFoundException absent = Assert.ThrowsException<FileNotFoundException>(
            () => LumaBridgeShaderSource.Load(missing));
        StringAssert.Contains(absent.Message, "vertex shader asset");

        InvalidDataException empty = Assert.ThrowsException<InvalidDataException>(() =>
            LumaBridgeShaderSource.Load(AssetManager(TextAsset("   "), TextAsset("   "))));
        StringAssert.Contains(empty.Message, "is empty");

        InvalidDataException version = Assert.ThrowsException<InvalidDataException>(() =>
            LumaBridgeShaderSource.Load(AssetManager(
                TextAsset("void main() {}"),
                TextAsset("#version 330 core\n"))));
        StringAssert.Contains(version.Message, "must begin with '#version 330 core'");

        string missingRoot = Path.Combine(
            Path.GetTempPath(),
            $"vintagertx-luma-bridge-missing-{Guid.NewGuid():N}");
        FileNotFoundException missingFile = Assert.ThrowsException<FileNotFoundException>(() =>
            LumaBridgeShaderSource.LoadFromFileSystem(missingRoot));
        StringAssert.Contains(missingFile.Message, "vertex shader file is missing");
    }

    /// <summary>Creates a deterministic asset-manager double for two shader assets.</summary>
    /// <param name="vertex">Optional vertex asset.</param>
    /// <param name="fragment">Optional fragment asset.</param>
    /// <returns>Asset manager that resolves only the two canonical locations.</returns>
    private static IAssetManager AssetManager(IAsset? vertex, IAsset? fragment) =>
        RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) =>
        {
            if (method.Name != "TryGet")
            {
                return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            }

            return arguments![0]!.ToString() == LumaBridgeShaderSource.VertexAssetCode
                ? vertex
                : fragment;
        });

    /// <summary>Creates an asset double that returns the supplied text.</summary>
    /// <param name="source">Text returned by <c>ToText</c>.</param>
    /// <returns>Deterministic text asset.</returns>
    private static IAsset TextAsset(string source) =>
        RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "ToText"
                ? source
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
}
