using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>Validates the entity-atlas ownership and dynamic-surface shader contracts.</summary>
[TestClass]
public sealed class PbrEntityRendererTests
{
    /// <summary>Verifies entity sidecars cannot be confused with block, item, or scalar maps.</summary>
    [TestMethod]
    public void EntitySidecarFilterAcceptsOnlyEntityNormalMaps()
    {
        Assert.IsTrue(PbrEntityRenderer.IsEntityNormalSidecar(
            new AssetLocation("game", "textures/entity/wolf/body_n.png")));
        Assert.IsFalse(PbrEntityRenderer.IsEntityNormalSidecar(
            new AssetLocation("game", "textures/entity/wolf/body_r.png")));
        Assert.IsFalse(PbrEntityRenderer.IsEntityNormalSidecar(
            new AssetLocation("game", "textures/item/tool_n.png")));
        Assert.IsFalse(PbrEntityRenderer.IsEntityNormalSidecar(
            new AssetLocation("game", "textures/block/stone_n.png")));
    }

    /// <summary>Verifies canonical source identities map to the public entity-atlas key format.</summary>
    [TestMethod]
    public void EntityAtlasIdentityPreservesDomainAndRejectsAmbiguousSources()
    {
        AssetLocation key = PbrEntityRenderer.ToEntityAtlasLocation(
            new AssetLocation("creatures", "textures/entity/Wolf/Body.png"));
        Assert.AreEqual("creatures", key.Domain);
        Assert.AreEqual("entity/wolf/body", key.Path);
        StringAssert.Contains(
            Assert.ThrowsException<ArgumentException>(() => PbrEntityRenderer.ToEntityAtlasLocation(
                new AssetLocation("game", "entity/wolf/body"))).Message,
            "canonical");
    }

    /// <summary>Verifies uploads require exact page-zero texture ownership and exclude the sentinel.</summary>
    [TestMethod]
    public void EntityAtlasPositionRequiresExactOwnedPage()
    {
        TextureAtlasPosition unknown = Position(99, 0);
        TextureAtlasPosition owned = Position(42, 0);
        Assert.IsTrue(PbrEntityRenderer.IsUsableAtlasPosition(owned, unknown, 42));
        Assert.IsFalse(PbrEntityRenderer.IsUsableAtlasPosition(null, unknown, 42));
        Assert.IsFalse(PbrEntityRenderer.IsUsableAtlasPosition(unknown, unknown, 99));
        Assert.IsFalse(PbrEntityRenderer.IsUsableAtlasPosition(Position(42, 1), unknown, 42));
        Assert.IsFalse(PbrEntityRenderer.IsUsableAtlasPosition(Position(41, 0), unknown, 42));
    }

    /// <summary>Verifies presence marking preserves material bits and rejects incomplete texels.</summary>
    [TestMethod]
    public void EntityPresenceBitMarksEveryCompleteRgbaTexel()
    {
        byte[] pixels = [1, 2, 3, 0, 4, 5, 6, 65];
        PbrEntityRenderer.MarkEntitySurfacePresence(pixels);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 128, 4, 5, 6, 193 }, pixels);
        PbrEntityRenderer.MarkEntitySurfacePresence([]);
        Assert.ThrowsException<ArgumentException>(() =>
            PbrEntityRenderer.MarkEntitySurfacePresence([1, 2, 3]));
    }

    /// <summary>Verifies entity PBR uses a second high sampler and fails on eight-unit hardware.</summary>
    [TestMethod]
    public void EntitySamplerSelectionNeverAliasesTerrainOrVanillaUnits()
    {
        Assert.AreEqual(30, PbrEntityRenderer.SelectEntityPbrTextureUnit(32));
        Assert.AreEqual(13, PbrEntityRenderer.SelectEntityPbrTextureUnit(17));
        Assert.AreEqual(13, PbrEntityRenderer.SelectEntityPbrTextureUnit(16));
        Assert.AreEqual(7, PbrEntityRenderer.SelectEntityPbrTextureUnit(9));
        StringAssert.Contains(
            Assert.ThrowsException<InvalidOperationException>(() =>
                PbrEntityRenderer.SelectEntityPbrTextureUnit(8)).Message,
            "second safe");
    }

    /// <summary>
    /// Verifies the entity shader samples albedo only from the vanilla entity atlas and emits a
    /// negative dynamic marker plus exact categorical material bits.
    /// </summary>
    [TestMethod]
    public void EntityShaderKeepsAlbedoIdentityAndPublishesPbrPayload()
    {
        string root = TestPaths.FindRepositoryRoot();
        string shader = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "game",
            "shaders",
            "entityanimated.fsh"));

        StringAssert.Contains(shader, "uniform sampler2D entityTex;");
        StringAssert.Contains(shader, "uniform sampler2D vintagertxEntityMaterialTex;");
        StringAssert.Contains(shader, "vec4 unlitTexColor = texture(entityTex, uv);");
        StringAssert.Contains(shader, "? -float(1 + roughnessBits * 32 + albedoBits) / 1025.0");
        StringAssert.Contains(shader, "float(pbrMaterialBits) / 255.0");
        Assert.IsFalse(shader.Contains(
            "outColor = texture(vintagertxEntityMaterialTex",
            StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains(
            "texColor = texture(vintagertxEntityMaterialTex",
            StringComparison.Ordinal));
    }

    /// <summary>Verifies the display pass never reconstructs a dynamic entity from voxel chroma.</summary>
    [TestMethod]
    public void DisplayShaderUsesRasterBackedDynamicAlbedo()
    {
        string root = TestPaths.FindRepositoryRoot();
        string shader = File.ReadAllText(Path.Combine(
            root,
            "src",
            "VintageRTX",
            "assets",
            "vintagertx",
            "shaders",
            "display.frag"));

        StringAssert.Contains(shader, "float dynamicSurface = step(packedSurfaceAlpha, -0.0005);");
        StringAssert.Contains(shader, "float packedSurfaceValue = abs(packedSurfaceAlpha);");
        StringAssert.Contains(shader, "vec3 dynamicSurfaceAlbedo = authoredAlbedoLuminance >= 0.0");
        StringAssert.Contains(shader, "? dynamicSurfaceAlbedo");
        StringAssert.Contains(shader, "* (1.0 - dynamicSurface)");
    }

    /// <summary>Creates a minimal mutable atlas position for ownership tests.</summary>
    /// <param name="textureId">Owning OpenGL texture.</param>
    /// <param name="atlasNumber">Owning atlas page.</param>
    /// <returns>A deterministic non-sentinel rectangle.</returns>
    private static TextureAtlasPosition Position(int textureId, byte atlasNumber) => new()
    {
        atlasTextureId = textureId,
        atlasNumber = atlasNumber,
        x1 = 0,
        y1 = 0,
        x2 = 1,
        y2 = 1
    };
}
