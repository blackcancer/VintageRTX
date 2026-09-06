using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>Exercises the dedicated reflection-isolation shader asset boundary.</summary>
[TestClass]
public sealed class ReflectionIsolationShaderSourceCoverageTests
{
    /// <summary>Loads both canonical sources unchanged when their GLSL versions are valid.</summary>
    [TestMethod]
    public void LoadReturnsCanonicalValidatedSources()
    {
        const string vertex = "#version 330 core\nvoid main() {}";
        const string fragment = "#version 330 core\nout vec4 color;";

        DisplayShaderProgramSource source = ReflectionIsolationShaderSource.Load(
            Assets(TextAsset(vertex), TextAsset(fragment)));

        Assert.AreEqual(vertex, source.Vertex);
        Assert.AreEqual(fragment, source.Fragment);
    }

    /// <summary>Rejects a null catalog and reports each independently missing shader identity.</summary>
    [TestMethod]
    public void LoadRejectsNullAndMissingAssets()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ReflectionIsolationShaderSource.Load(null!));

        FileNotFoundException missingVertex = Assert.ThrowsException<FileNotFoundException>(() =>
            ReflectionIsolationShaderSource.Load(Assets(null, null)));
        StringAssert.Contains(missingVertex.Message, DisplayShaderSource.VertexAssetCode);

        FileNotFoundException missingFragment = Assert.ThrowsException<FileNotFoundException>(() =>
            ReflectionIsolationShaderSource.Load(Assets(TextAsset("#version 330 core\n"), null)));
        StringAssert.Contains(missingFragment.Message, ReflectionIsolationShaderSource.FragmentAssetCode);
    }

    /// <summary>Rejects an invalid version directive in either stage.</summary>
    [TestMethod]
    public void LoadRejectsWrongVertexAndFragmentVersions()
    {
        InvalidDataException badVertex = Assert.ThrowsException<InvalidDataException>(() =>
            ReflectionIsolationShaderSource.Load(Assets(
                TextAsset("#version 450 core\n"),
                TextAsset("#version 330 core\n"))));
        StringAssert.Contains(badVertex.Message, "must begin with '#version 330 core'");

        InvalidDataException badFragment = Assert.ThrowsException<InvalidDataException>(() =>
            ReflectionIsolationShaderSource.Load(Assets(
                TextAsset("#version 330 core\n"),
                TextAsset("\n#version 330 core\n"))));
        StringAssert.Contains(badFragment.Message, "must begin with '#version 330 core'");
    }

    /// <summary>Creates a minimal asset catalog containing the requested shader doubles.</summary>
    /// <param name="vertex">Vertex asset, or null to model a missing source.</param>
    /// <param name="fragment">Fragment asset, or null to model a missing source.</param>
    /// <returns>A deterministic shader asset manager.</returns>
    private static IAssetManager Assets(IAsset? vertex, IAsset? fragment) =>
        RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) =>
        {
            if (method.Name == "TryGet")
            {
                string location = arguments![0]!.ToString()!;
                return location == DisplayShaderSource.VertexAssetCode ? vertex : fragment;
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

    /// <summary>Creates one loaded text asset.</summary>
    /// <param name="source">Text returned by the asset.</param>
    /// <returns>A deterministic asset double.</returns>
    private static IAsset TextAsset(string source) =>
        RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) => method.Name switch
        {
            "ToText" => source,
            "IsLoaded" => true,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
}
