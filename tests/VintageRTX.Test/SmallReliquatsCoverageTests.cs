using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Contains deterministic regression checks for small Reliquats Coverage.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SmallReliquatsCoverageTests
{
    /// <summary>
    /// Verifies the configuration Migration Upgrades Every Superseded Default regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ConfigurationMigrationUpgradesEverySupersededDefault()
    {
        VintageRtxConfig legacyTransport = new()
        {
            SchemaVersion = 2,
            PointLightRadius = 28.0f,
            GpuBudgetMilliseconds = 1.60f,
            IndirectLightStrength = 0.32f,
            PointLightBounceStrength = 0.28f,
            SunLightStrength = 0.28f,
            Contrast = 1.06f,
            Saturation = 1.04f,
            Vibrance = 0.08f,
            Vignette = 0.10f
        };

        Assert.IsTrue(legacyTransport.Migrate());
        Assert.AreEqual(VintageRtxConfig.CurrentSchemaVersion, legacyTransport.SchemaVersion);
        Assert.AreEqual(18.0f, legacyTransport.PointLightRadius);
        Assert.AreEqual(2.00f, legacyTransport.GpuBudgetMilliseconds);
        Assert.AreEqual(0.82f, legacyTransport.IndirectLightStrength);
        Assert.AreEqual(0.90f, legacyTransport.PointLightBounceStrength);
        Assert.AreEqual(1.10f, legacyTransport.SunLightStrength);
        Assert.AreEqual(1.0f, legacyTransport.Contrast);
        Assert.AreEqual(1.0f, legacyTransport.Saturation);
        Assert.AreEqual(0.0f, legacyTransport.Vibrance);
        Assert.AreEqual(0.04f, legacyTransport.Vignette);
        Assert.AreEqual(0.025f, legacyTransport.PointLightSourceRadius);
        Assert.AreEqual(VintageRtxRenderProfile.Custom, legacyTransport.RenderProfile);

        VintageRtxConfig schemaTenBudget = new()
        {
            SchemaVersion = 10,
            GpuBudgetMilliseconds = 3.50f
        };
        Assert.IsTrue(schemaTenBudget.Migrate());
        Assert.AreEqual(2.00f, schemaTenBudget.GpuBudgetMilliseconds);

        VintageRtxConfig schemaTwelve = new()
        {
            SchemaVersion = 12,
            RenderProfile = VintageRtxRenderProfile.Ultra,
            RayCount = 2
        };
        Assert.IsTrue(schemaTwelve.Migrate());
        Assert.AreEqual(VintageRtxConfig.CurrentSchemaVersion, schemaTwelve.SchemaVersion);
        Assert.AreEqual(VintageRtxRenderProfile.Custom, schemaTwelve.RenderProfile);
        Assert.AreEqual(2, schemaTwelve.RayCount);

        VintageRtxConfig customSourceSize = new()
        {
            SchemaVersion = 13,
            PointLightSourceRadius = 0.04f
        };
        Assert.IsTrue(customSourceSize.Migrate());
        Assert.AreEqual(0.04f, customSourceSize.PointLightSourceRadius);

        VintageRtxConfig invalidDebugView = new()
        {
            SchemaVersion = VintageRtxConfig.CurrentSchemaVersion,
            DebugView = (VintageRtxDebugView)int.MaxValue
        };
        invalidDebugView.Clamp();
        Assert.AreEqual(VintageRtxDebugView.Final, invalidDebugView.DebugView);
        Assert.IsFalse(invalidDebugView.Migrate(), "current schema migration must be idempotent");
    }

    /// <summary>
    /// Verifies the world Inputs Sanitize Engine Signals And Classify Public Assets regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void WorldInputsSanitizeEngineSignalsAndClassifyPublicAssets()
    {
        Vec3d wind = new(double.MaxValue, 0.0, -double.MaxValue);
        ClimateCondition climate = new() { Rainfall = 2.0f };
        int rainHeight = 20;
        IBlockAccessor accessor = RuntimeCoverageDispatchProxy.Create<IBlockAccessor>((method, _) =>
            method.Name switch
            {
                "GetWindSpeedAt" => wind,
                "GetClimateAt" => climate,
                "GetRainMapHeightAt" => rainHeight,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        LiquidSurfaceWorldInputs inputs = new(dimensionId: 2);

        LiquidSurfaceForcing saturated = inputs.SampleEnvironment(accessor, 10.5, 21.0, -4.5);
        Assert.AreEqual(float.MaxValue, saturated.WindX);
        Assert.AreEqual(-float.MaxValue, saturated.WindZ);
        Assert.AreEqual(float.MaxValue, saturated.WindVelocityXMetresPerSecond);
        Assert.AreEqual(-float.MaxValue, saturated.WindVelocityZMetresPerSecond);
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.MaximumRainfallRateMetresPerSecond,
            saturated.Rainfall);
        Assert.AreEqual(saturated.Rainfall, saturated.RainfallRateMetresPerSecond);

        wind.Set(double.NaN, 0.0, double.PositiveInfinity);
        climate.Rainfall = float.NaN;
        LiquidSurfaceForcing sanitized = inputs.SampleEnvironment(accessor, 10.5, 21.0, -4.5);
        Assert.AreEqual(0.0f, sanitized.WindX);
        Assert.AreEqual(0.0f, sanitized.WindZ);
        Assert.AreEqual(0.0f, sanitized.Rainfall);
        Assert.IsTrue(inputs.IsRainExposed(accessor, 10, 20.0f, -5));
        Assert.IsFalse(inputs.IsRainExposed(accessor, 10, 19.999f, -5));

        Assert.AreEqual(
            LiquidEntitySurfaceClass.Generic,
            LiquidSurfaceEntityClassifier.Classify(code: null, isCreature: false));
        Assert.AreEqual(
            LiquidEntitySurfaceClass.Bobber,
            LiquidSurfaceEntityClassifier.Classify(new AssetLocation("game:bobber"), isCreature: false));
        Assert.AreEqual(
            LiquidEntitySurfaceClass.ThrownStone,
            LiquidSurfaceEntityClassifier.Classify(new AssetLocation("mod:thrownstone-basalt"), isCreature: false));
        Assert.AreEqual(
            LiquidEntitySurfaceClass.Fish,
            LiquidSurfaceEntityClassifier.Classify(new AssetLocation("game:fish-trout"), isCreature: true));
        Assert.AreEqual(
            LiquidEntitySurfaceClass.Generic,
            LiquidSurfaceEntityClassifier.Classify(new AssetLocation("game:fish-decoration"), isCreature: false));
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            LiquidSurfaceWorldInputs.EstimateDroppedItemPseudoMassKilograms(0));
        Assert.AreEqual(
            0.70f,
            LiquidSurfaceWorldInputs.EstimateDroppedItemPseudoMassKilograms(4),
            0.0001f);
        Assert.AreEqual(
            2.80f,
            LiquidSurfaceWorldInputs.EstimateDroppedItemPseudoMassKilograms(64),
            0.0001f);
        Assert.AreEqual(
            2.80f,
            LiquidSurfaceWorldInputs.EstimateDroppedItemPseudoMassKilograms(4_096),
            0.0001f);

        EntityPlayer entity = new() { EntityId = 73 };
        entity.Pos.SetPos(1.25, 2.5, 3.75);
        entity.Pos.Motion.Set(double.MaxValue, double.NaN, double.NegativeInfinity);
        LiquidEntitySurfaceSample entitySample = LiquidSurfaceWorldInputs.SampleEntity(entity);
        Assert.AreEqual(73L, entitySample.EntityId);
        Assert.AreEqual(1.25, entitySample.WorldX);
        Assert.AreEqual(2.5, entitySample.WorldY);
        Assert.AreEqual(3.75, entitySample.WorldZ);
        Assert.AreEqual(float.MaxValue, entitySample.MotionX);
        Assert.AreEqual(0.0f, entitySample.MotionY);
        Assert.AreEqual(0.0f, entitySample.MotionZ);
        Assert.AreEqual(25.0f, entitySample.MassKilograms);
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.DefaultMotionSamplePeriodSeconds,
            entitySample.MotionSamplePeriodSeconds);

        typeof(Entity).GetProperty(nameof(Entity.Properties))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(entity, [new EntityProperties
            {
                Code = new AssetLocation("game:bobber"),
                Weight = 3.5f
            }]);
        LiquidEntitySurfaceSample classifiedEntitySample = LiquidSurfaceWorldInputs.SampleEntity(
            entity,
            motionSamplePeriodSeconds: 0.05f);
        Assert.AreEqual(LiquidEntitySurfaceClass.Bobber, classifiedEntitySample.SurfaceClass);
        Assert.AreEqual(3.5f, classifiedEntitySample.MassKilograms);
        Assert.AreEqual(0.05f, classifiedEntitySample.MotionSamplePeriodSeconds);

        EntityItem droppedItem = new()
        {
            EntityId = 74,
            Itemstack = new ItemStack(new Item(), 16)
        };
        droppedItem.Pos.Motion.Set(0.1, -0.2, 0.3);
        LiquidEntitySurfaceSample droppedSample = LiquidSurfaceWorldInputs.SampleEntity(
            droppedItem,
            motionSamplePeriodSeconds: 0.05f);
        Assert.AreEqual(LiquidEntitySurfaceClass.DroppedItem, droppedSample.SurfaceClass);
        Assert.AreEqual(1.40f, droppedSample.MassKilograms, 0.0001f);
        Assert.AreEqual(0.1f, droppedSample.MotionX, 0.0001f);

        EntityItem emptyDroppedItem = new() { EntityId = 75 };
        LiquidEntitySurfaceSample emptyDroppedSample = LiquidSurfaceWorldInputs.SampleEntity(
            emptyDroppedItem);
        Assert.AreEqual(
            LiquidSurfaceWorldInputs.ReferenceDroppedItemMassKilograms,
            emptyDroppedSample.MassKilograms);
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidSurfaceWorldInputs.SampleEntity(entity, 0.0f));
    }

    /// <summary>
    /// Verifies the shader Loader Contains Decode And Exclusive File Failures regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ShaderLoaderContainsDecodeAndExclusiveFileFailures()
    {
        Assert.ThrowsException<ArgumentNullException>(() => DisplayShaderSource.Load(null!));
        Assert.ThrowsException<ArgumentException>(() => DisplayShaderSource.LoadFromFileSystem("  "));

        IAsset unreadable = RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
        {
            if (method.Name == "ToText")
            {
                throw new IOException("fixture decode failure");
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        IAssetManager assets = RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, _) =>
            method.Name == "TryGet"
                ? unreadable
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        InvalidDataException decode = Assert.ThrowsException<InvalidDataException>(
            () => DisplayShaderSource.Load(assets));
        Assert.IsInstanceOfType<IOException>(decode.InnerException);

        string root = Path.Combine(Path.GetTempPath(), $"vintagertx-shader-lock-{Guid.NewGuid():N}");
        string shaders = Path.Combine(root, "assets", "vintagertx", "shaders");
        Directory.CreateDirectory(shaders);
        string vertex = Path.Combine(shaders, "display.vert");
        File.WriteAllText(vertex, "#version 330 core\nvoid main() {}\n");
        File.WriteAllText(Path.Combine(shaders, "display.frag"), "#version 330 core\nvoid main() {}\n");
        try
        {
            using FileStream lockFile = new(
                vertex,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            InvalidDataException read = Assert.ThrowsException<InvalidDataException>(
                () => DisplayShaderSource.LoadFromFileSystem(root));
            Assert.IsInstanceOfType<IOException>(read.InnerException);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
