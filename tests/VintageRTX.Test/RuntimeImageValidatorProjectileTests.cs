using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>Exercises exact-impact localization for projectile surface-field evidence.</summary>
[TestClass]
public sealed class RuntimeImageValidatorProjectileTests
{
    /// <summary>Proves localized rings pass while a spatially uniform field change is rejected.</summary>
    [TestMethod]
    public void LocalizedProjectileDeltaRejectsDiffuseWindOnlyChange()
    {
        using SKBitmap baseline = CreateField(128, 128, 0, 0, 0);
        using SKBitmap localized = CreateField(128, 128, 64, 64, 14);
        using SKBitmap diffuse = CreateField(128, 128, 0, 0, 0, diffuseCarrierDelta: 8);

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment accepted =
            RuntimeImageValidator.MeasureProjectileSurfaceDelta(
                baseline,
                localized,
                64.0,
                64.0);
        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment rejected =
            RuntimeImageValidator.MeasureProjectileSurfaceDelta(
                baseline,
                diffuse,
                64.0,
                64.0);

        Assert.IsTrue(accepted.Compatible);
        Assert.IsTrue(accepted.MeetsGate);
        Assert.IsTrue(accepted.PeakHeightDeltaMetres >= 0.004);
        Assert.IsTrue(accepted.LocalizationRatio >= 1.35);
        Assert.IsTrue(accepted.CoherentWaveRmsMetres > accepted.ControlWaveRmsMetres);
        Assert.IsTrue(accepted.WaveLocalizationRatio >= 1.30);
        Assert.IsTrue(rejected.Compatible);
        Assert.IsFalse(rejected.MeetsGate);
        Assert.IsTrue(rejected.LocalizationRatio < 1.10);
        Assert.IsTrue(rejected.WaveLocalizationRatio < 1.10);
    }

    /// <summary>Rejects translated global wind bands even when their signed height delta is large.</summary>
    [TestMethod]
    public void ProjectileDeltaRejectsTranslatedWindBands()
    {
        using SKBitmap baseline = CreateWindBands(128, 128, phase: 0.0);
        using SKBitmap translated = CreateWindBands(128, 128, phase: 0.55);

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment assessment =
            RuntimeImageValidator.MeasureProjectileSurfaceDelta(
                baseline,
                translated,
                64.0,
                64.0);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsFalse(assessment.MeetsGate);
        Assert.IsTrue(assessment.PeakHeightDeltaMetres > 0.00035);
        Assert.IsTrue(assessment.LocalSignalRmsMetres > 0.00012);
        Assert.IsTrue(assessment.WaveLocalizationRatio < 1.10);
    }

    /// <summary>Rejects a real local ring when the supplied contact anchor is elsewhere.</summary>
    [TestMethod]
    public void ProjectileDeltaRejectsWrongImpactAnchor()
    {
        using SKBitmap baseline = CreateField(128, 128, 0, 0, 0);
        using SKBitmap localized = CreateField(128, 128, 64, 64, 14);

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment assessment =
            RuntimeImageValidator.MeasureProjectileSurfaceDelta(
                baseline,
                localized,
                64.0,
                96.0);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsFalse(assessment.MeetsGate);
        Assert.IsTrue(assessment.PeakHeightDeltaMetres < 0.00035);
        Assert.IsTrue(assessment.CoherentWaveRmsMetres < 0.00005);
    }

    /// <summary>Validates saved-path parsing, screen anchors and distinct stone/arrow responses.</summary>
    [TestMethod]
    public void ProjectileSurfaceFieldValidationConsumesExactSavedPairsAndAnchors()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"vintagertx-projectile-field-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            DateTime origin = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Local);
            string stoneEarlier = SaveField(
                directory,
                "projectile-stone-baseline-earlier-surface-field",
                0,
                0,
                0,
                origin);
            string stonePrior = SaveField(
                directory,
                "projectile-stone-baseline-prior-surface-field",
                0,
                0,
                0,
                origin.AddMilliseconds(500));
            string stoneBaseline = SaveField(
                directory,
                "projectile-stone-baseline-surface-field",
                0,
                0,
                0,
                origin.AddMilliseconds(1_000));
            string stoneResponse = SaveField(
                directory,
                "projectile-stone-surface-field",
                40,
                64,
                14,
                origin.AddMilliseconds(1_500));
            string arrowEarlier = SaveField(
                directory,
                "projectile-arrow-baseline-earlier-surface-field",
                0,
                0,
                0,
                origin.AddSeconds(2));
            string arrowPrior = SaveField(
                directory,
                "projectile-arrow-baseline-prior-surface-field",
                0,
                0,
                0,
                origin.AddMilliseconds(2_500));
            string arrowBaseline = SaveField(
                directory,
                "projectile-arrow-baseline-surface-field",
                0,
                0,
                0,
                origin.AddSeconds(3));
            string arrowResponse = SaveField(
                directory,
                "projectile-arrow-surface-field",
                88,
                64,
                10,
                origin.AddMilliseconds(3_500));
            string log = string.Join(
                '\n',
                SavedPair(stoneEarlier),
                SavedPair(stonePrior),
                SavedPair(stoneBaseline),
                SavedPair(stoneResponse),
                SavedPair(arrowEarlier),
                SavedPair(arrowPrior),
                SavedPair(arrowBaseline),
                SavedPair(arrowResponse),
                "[VintageRTX.Test] Projectile impact screen anchor: sequence=1, projectile=stone, pixel=(99.00,99.00), viewport=128x128.",
                SurfaceFrames(1, "stone", 99, 99),
                "[VintageRTX.Test] Projectile impact screen anchor: sequence=7, projectile=stone, pixel=(40.00,64.00), viewport=128x128.",
                "[VintageRTX.Test] Projectile impact screen anchor: sequence=11, projectile=arrow, pixel=(88.00,64.00), viewport=128x128.",
                SurfaceFrames(7, "stone", 40, 64),
                SurfaceFrames(11, "arrow", 88, 64),
                WavePacket(7, 0.010, 1.000),
                WavePacket(11, 0.008, 1.000),
                SynchronizedCapture("stone", 7),
                SynchronizedCapture("arrow", 11));

            CollectionAssert.AreEqual(
                Array.Empty<string>(),
                RuntimeImageValidator.ValidateProjectileSurfaceFieldDeltas(log).ToArray());

            string missingAnchor = log.Replace(
                "[VintageRTX.Test] Projectile impact screen anchor: sequence=11, projectile=arrow, pixel=(88.00,64.00), viewport=128x128.",
                string.Empty,
                StringComparison.Ordinal);
            Assert.IsTrue(RuntimeImageValidator.ValidateProjectileSurfaceFieldDeltas(missingAnchor)
                .Any(static failure => failure.Contains("arrow", StringComparison.Ordinal)
                    && failure.Contains("anchor", StringComparison.Ordinal)));

            string ambiguousCallback = string.Join('\n', log, SynchronizedCapture("stone", 12));
            Assert.IsTrue(RuntimeImageValidator.ValidateProjectileSurfaceFieldDeltas(ambiguousCallback)
                .Any(static failure => failure.Contains("stone", StringComparison.Ordinal)
                    && failure.Contains("ambiguous", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Proves a foreshortened world ring is evaluated as a world circle, not a pixel circle.</summary>
    [TestMethod]
    public void WorldProjectedProjectileDeltaUsesLiquidPlaneTangents()
    {
        using SKBitmap earlier = CreateField(256, 192, 0, 0, 0);
        using SKBitmap prior = CreateField(256, 192, 0, 0, 0);
        using SKBitmap baseline = CreateField(256, 192, 0, 0, 0);
        RuntimeImageValidator.ProjectileSurfaceFrame contact = new(
            new System.Numerics.Vector2(128, 96),
            new System.Numerics.Vector2(18, -7),
            new System.Numerics.Vector2(5, 4),
            2.0);
        using SKBitmap response = CreateWorldProjectedRing(256, 192, contact, 16);
        RuntimeImageValidator.ProjectileSurfaceFrame[] controls =
        [
            contact with { Center = new System.Numerics.Vector2(48, 48) },
            contact with { Center = new System.Numerics.Vector2(208, 48) },
            contact with { Center = new System.Numerics.Vector2(48, 144) },
            contact with { Center = new System.Numerics.Vector2(208, 144) }
        ];

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment assessment =
            RuntimeImageValidator.MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                response,
                new RuntimeImageValidator.TemporalCounterfactualWeights(1.0, -3.0, 3.0),
                new RuntimeImageValidator.ProjectileWavePacket(1, 0.01, 2.0 * Math.PI / 5.6),
                contact,
                controls);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsTrue(assessment.MeetsGate);
        Assert.IsTrue(assessment.CoherentWaveRmsMetres > assessment.ControlWaveRmsMetres);
        Assert.IsTrue(assessment.WaveLocalizationRatio >= 1.30);
    }

    /// <summary>
    /// Proves three undisturbed samples remove wind evolution while retaining only the ring
    /// introduced after impact. This is a temporal counterfactual, not a gain applied to the ring.
    /// </summary>
    [TestMethod]
    public void WorldProjectedProjectileDeltaExtrapolatesWindWithoutAmplifyingImpact()
    {
        RuntimeImageValidator.ProjectileSurfaceFrame contact = new(
            new System.Numerics.Vector2(128, 96),
            new System.Numerics.Vector2(18, -7),
            new System.Numerics.Vector2(5, 4),
            2.0);
        RuntimeImageValidator.ProjectileSurfaceFrame[] controls =
        [
            contact with { Center = new System.Numerics.Vector2(48, 48) },
            contact with { Center = new System.Numerics.Vector2(208, 48) },
            contact with { Center = new System.Numerics.Vector2(48, 144) },
            contact with { Center = new System.Numerics.Vector2(208, 144) }
        ];
        using SKBitmap earlier = CreateLinearWindField(256, 192, timeStep: 0);
        using SKBitmap prior = CreateLinearWindField(256, 192, timeStep: 1);
        using SKBitmap baseline = CreateLinearWindField(256, 192, timeStep: 2);
        using SKBitmap windOnly = CreateLinearWindField(256, 192, timeStep: 3);
        using SKBitmap response = CreateLinearWindField(256, 192, timeStep: 3);
        AddWorldProjectedRing(response, contact, 16);
        RuntimeImageValidator.TemporalCounterfactualWeights counterfactual = new(1.0, -3.0, 3.0);

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment accepted =
            RuntimeImageValidator.MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                response,
                counterfactual,
                new RuntimeImageValidator.ProjectileWavePacket(1, 0.01, 2.0 * Math.PI / 5.6),
                contact,
                controls);
        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment rejected =
            RuntimeImageValidator.MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                windOnly,
                counterfactual,
                new RuntimeImageValidator.ProjectileWavePacket(1, 0.01, 2.0 * Math.PI / 5.6),
                contact,
                controls);

        Assert.IsTrue(accepted.Compatible);
        Assert.IsTrue(accepted.MeetsGate);
        Assert.IsTrue(accepted.WaveLocalizationRatio >= 1.30);
        Assert.IsTrue(rejected.Compatible);
        Assert.IsFalse(rejected.MeetsGate);
        Assert.AreEqual(0.0, rejected.CoherentWaveRmsMetres, 1.0e-12);
    }

    /// <summary>
    /// Proves the three-frame counterfactual removes spatially varying wind acceleration, which a
    /// two-frame velocity extrapolation leaves as camera-wide residual energy.
    /// </summary>
    [TestMethod]
    public void WorldProjectedProjectileDeltaRemovesQuadraticWindCurvature()
    {
        RuntimeImageValidator.ProjectileSurfaceFrame contact = new(
            new System.Numerics.Vector2(128, 96),
            new System.Numerics.Vector2(18, -7),
            new System.Numerics.Vector2(5, 4),
            2.0);
        RuntimeImageValidator.ProjectileSurfaceFrame[] controls =
        [
            contact with { Center = new System.Numerics.Vector2(48, 48) },
            contact with { Center = new System.Numerics.Vector2(208, 48) },
            contact with { Center = new System.Numerics.Vector2(48, 144) },
            contact with { Center = new System.Numerics.Vector2(208, 144) }
        ];
        using SKBitmap earlier = CreateQuadraticWindField(256, 192, timeStep: 0);
        using SKBitmap prior = CreateQuadraticWindField(256, 192, timeStep: 1);
        using SKBitmap baseline = CreateQuadraticWindField(256, 192, timeStep: 2);
        using SKBitmap windOnly = CreateQuadraticWindField(256, 192, timeStep: 3);
        using SKBitmap response = CreateQuadraticWindField(256, 192, timeStep: 3);
        AddWorldProjectedRing(response, contact, 16);
        RuntimeImageValidator.TemporalCounterfactualWeights counterfactual = new(1.0, -3.0, 3.0);

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment accepted =
            RuntimeImageValidator.MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                response,
                counterfactual,
                new RuntimeImageValidator.ProjectileWavePacket(1, 0.01, 2.0 * Math.PI / 5.6),
                contact,
                controls);
        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment rejected =
            RuntimeImageValidator.MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                windOnly,
                counterfactual,
                new RuntimeImageValidator.ProjectileWavePacket(1, 0.01, 2.0 * Math.PI / 5.6),
                contact,
                controls);

        Assert.IsTrue(accepted.MeetsGate);
        Assert.IsTrue(accepted.WaveLocalizationRatio >= 1.30);
        Assert.IsTrue(rejected.Compatible);
        Assert.IsFalse(rejected.MeetsGate);
        Assert.AreEqual(0.0, rejected.LocalSignalRmsMetres, 1.0e-12);
        Assert.AreEqual(0.0, rejected.CoherentWaveRmsMetres, 1.0e-12);
    }

    /// <summary>
    /// Proves a sub-Nyquist carrier is never matched directly: its resolved, anti-aliased normal
    /// envelope provides the localized geometric witness instead.
    /// </summary>
    [TestMethod]
    public void WorldProjectedProjectileDeltaUsesResolvedNormalEnvelopeBelowNyquist()
    {
        RuntimeImageValidator.ProjectileSurfaceFrame contact = new(
            new System.Numerics.Vector2(128, 96),
            new System.Numerics.Vector2(50, 0),
            new System.Numerics.Vector2(0, 50),
            0.5);
        RuntimeImageValidator.ProjectileSurfaceFrame[] controls =
        [
            contact with { Center = new System.Numerics.Vector2(48, 48) },
            contact with { Center = new System.Numerics.Vector2(208, 48) },
            contact with { Center = new System.Numerics.Vector2(48, 144) },
            contact with { Center = new System.Numerics.Vector2(208, 144) }
        ];
        using SKBitmap earlier = CreateField(256, 192, 0, 0, 0);
        using SKBitmap prior = CreateField(256, 192, 0, 0, 0);
        using SKBitmap baseline = CreateField(256, 192, 0, 0, 0);
        using SKBitmap response = CreateField(256, 192, 0, 0, 0);
        AddWorldProjectedNormalEnvelope(response, contact, 0.15, 0.05, 3, 60);

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment assessment =
            RuntimeImageValidator.MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                response,
                new RuntimeImageValidator.TemporalCounterfactualWeights(1.0, -3.0, 3.0),
                new RuntimeImageValidator.ProjectileWavePacket(3, 0.001, 0.01),
                contact,
                controls);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsTrue(assessment.MeetsGate);
        Assert.IsTrue(assessment.CoherentWaveRmsMetres > assessment.ControlWaveRmsMetres);
        Assert.IsTrue(assessment.WaveLocalizationRatio >= 1.30);
    }

    /// <summary>Rejects image-frequency aliases when no resolved normal envelope exists.</summary>
    [TestMethod]
    public void WorldProjectedProjectileDeltaRejectsSubNyquistHeightAlias()
    {
        RuntimeImageValidator.ProjectileSurfaceFrame contact = new(
            new System.Numerics.Vector2(128, 96),
            new System.Numerics.Vector2(50, 0),
            new System.Numerics.Vector2(0, 50),
            0.5);
        RuntimeImageValidator.ProjectileSurfaceFrame[] controls =
        [
            contact with { Center = new System.Numerics.Vector2(48, 48) },
            contact with { Center = new System.Numerics.Vector2(208, 48) },
            contact with { Center = new System.Numerics.Vector2(48, 144) },
            contact with { Center = new System.Numerics.Vector2(208, 144) }
        ];
        using SKBitmap earlier = CreateField(256, 192, 0, 0, 0);
        using SKBitmap prior = CreateField(256, 192, 0, 0, 0);
        using SKBitmap baseline = CreateField(256, 192, 0, 0, 0);
        using SKBitmap response = CreateField(256, 192, 0, 0, 0);
        AddWorldProjectedAliasedHeightCarrier(response, contact, 0.01, 14);

        RuntimeImageValidator.ProjectileSurfaceDeltaAssessment assessment =
            RuntimeImageValidator.MeasureWorldProjectedProjectileSurfaceDelta(
                earlier,
                prior,
                baseline,
                response,
                new RuntimeImageValidator.TemporalCounterfactualWeights(1.0, -3.0, 3.0),
                new RuntimeImageValidator.ProjectileWavePacket(3, 0.001, 0.01),
                contact,
                controls);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsFalse(assessment.MeetsGate);
        Assert.AreEqual(0.0, assessment.CoherentWaveRmsMetres, 1.0e-12);
        Assert.IsTrue(assessment.PeakHeightDeltaMetres > 0.00035);
    }

    /// <summary>Confirms dimension and anchor bounds fail without indexing either bitmap.</summary>
    [TestMethod]
    public void ProjectileSurfaceDeltaRejectsIncompatibleInputs()
    {
        using SKBitmap small = CreateField(16, 16, 0, 0, 0);
        using SKBitmap normal = CreateField(64, 64, 0, 0, 0);
        using SKBitmap mismatched = CreateField(65, 64, 0, 0, 0);
        Assert.IsFalse(RuntimeImageValidator.MeasureProjectileSurfaceDelta(
            small,
            small,
            8.0,
            8.0).Compatible);
        Assert.IsFalse(RuntimeImageValidator.MeasureProjectileSurfaceDelta(
            normal,
            mismatched,
            8.0,
            8.0).Compatible);
        Assert.IsFalse(RuntimeImageValidator.MeasureProjectileSurfaceDelta(
            normal,
            normal,
            -100.0,
            8.0).Compatible);
    }

    /// <summary>Creates one complementary red/blue surface field with an optional local ring.</summary>
    private static SKBitmap CreateField(
        int width,
        int height,
        int impactX,
        int impactY,
        int amplitude,
        int diffuseCarrierDelta = 1)
    {
        SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int diffuse = ((x + y) & 1) == 0
                    ? diffuseCarrierDelta
                    : -diffuseCarrierDelta;
                double distance = Math.Sqrt(
                    (x - impactX) * (double)(x - impactX)
                    + (y - impactY) * (double)(y - impactY));
                int ring = amplitude > 0 && distance <= 11.0
                    ? (int)Math.Round(amplitude * Math.Sin(distance * 1.35))
                    : 0;
                int carrier = Math.Clamp(diffuse + ring, -60, 60);
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(128 + carrier),
                        (byte)(16 + Math.Abs(ring) / 2),
                        (byte)(128 - carrier),
                        255));
            }
        }

        return bitmap;
    }

    /// <summary>Creates perspective-neutral horizontal wind bands at one temporal phase.</summary>
    private static SKBitmap CreateWindBands(int width, int height, double phase)
    {
        SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            int carrier = (int)Math.Round(24.0 * Math.Sin(y * 0.22 + phase));
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(128 + carrier),
                        (byte)(24 + Math.Abs(carrier) / 3),
                        (byte)(128 - carrier),
                        255));
            }
        }

        return bitmap;
    }

    /// <summary>Creates one ring circular in world X/Z but elliptical in framebuffer pixels.</summary>
    private static SKBitmap CreateWorldProjectedRing(
        int width,
        int height,
        RuntimeImageValidator.ProjectileSurfaceFrame frame,
        int amplitude)
    {
        SKBitmap bitmap = CreateField(width, height, 0, 0, 0);
        AddWorldProjectedRing(bitmap, frame, amplitude);
        return bitmap;
    }

    /// <summary>Adds one world-circular signed ring to an existing encoded field.</summary>
    private static void AddWorldProjectedRing(
        SKBitmap bitmap,
        RuntimeImageValidator.ProjectileSurfaceFrame frame,
        int amplitude)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        System.Numerics.Vector2 basisX = frame.ScreenPerWorldX;
        System.Numerics.Vector2 basisZ = frame.ScreenPerWorldZ;
        double determinant = basisX.X * basisZ.Y - basisZ.X * basisX.Y;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double pixelX = x - frame.Center.X;
                double pixelY = y - frame.Center.Y;
                double worldX = (pixelX * basisZ.Y - basisZ.X * pixelY) / determinant;
                double worldZ = (basisX.X * pixelY - pixelX * basisX.Y) / determinant;
                double radius = Math.Sqrt(worldX * worldX + worldZ * worldZ);
                if (radius > frame.SupportRadiusWorldBlocks)
                {
                    continue;
                }

                int ring = (int)Math.Round(amplitude * Math.Sin(radius * 5.6));
                SKColor existing = bitmap.GetPixel(x, y);
                int existingCarrier = (existing.Red - existing.Blue) / 2;
                int carrier = Math.Clamp(existingCarrier + ring, -60, 60);
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(128 + carrier),
                        (byte)Math.Clamp(existing.Green + Math.Abs(ring) / 2, 0, 191),
                        (byte)(128 - carrier),
                        255));
            }
        }
    }

    /// <summary>Adds a resolved Gaussian slope envelope for a sub-Nyquist packet.</summary>
    private static void AddWorldProjectedNormalEnvelope(
        SKBitmap bitmap,
        RuntimeImageValidator.ProjectileSurfaceFrame frame,
        double frontRadius,
        double sigma,
        int heightChannels,
        int normalChannels)
    {
        System.Numerics.Vector2 basisX = frame.ScreenPerWorldX;
        System.Numerics.Vector2 basisZ = frame.ScreenPerWorldZ;
        double determinant = basisX.X * basisZ.Y - basisZ.X * basisX.Y;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                double pixelX = x - frame.Center.X;
                double pixelY = y - frame.Center.Y;
                double worldX = (pixelX * basisZ.Y - basisZ.X * pixelY) / determinant;
                double worldZ = (basisX.X * pixelY - pixelX * basisX.Y) / determinant;
                double radius = Math.Sqrt(worldX * worldX + worldZ * worldZ);
                if (radius > frame.SupportRadiusWorldBlocks)
                {
                    continue;
                }

                double envelope = Math.Exp(-0.5 * Math.Pow((radius - frontRadius) / sigma, 2.0));
                int height = (int)Math.Round(heightChannels * envelope);
                int normal = (int)Math.Round(normalChannels * envelope);
                SKColor existing = bitmap.GetPixel(x, y);
                int existingCarrier = (existing.Red - existing.Blue) / 2;
                int carrier = Math.Clamp(existingCarrier + height, -60, 60);
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(128 + carrier),
                        (byte)Math.Clamp(existing.Green + normal, 0, 191),
                        (byte)(128 - carrier),
                        255));
            }
        }
    }

    /// <summary>Adds a height-only carrier too fine for the supplied screen differential.</summary>
    private static void AddWorldProjectedAliasedHeightCarrier(
        SKBitmap bitmap,
        RuntimeImageValidator.ProjectileSurfaceFrame frame,
        double wavelength,
        int amplitude)
    {
        System.Numerics.Vector2 basisX = frame.ScreenPerWorldX;
        System.Numerics.Vector2 basisZ = frame.ScreenPerWorldZ;
        double determinant = basisX.X * basisZ.Y - basisZ.X * basisX.Y;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                double pixelX = x - frame.Center.X;
                double pixelY = y - frame.Center.Y;
                double worldX = (pixelX * basisZ.Y - basisZ.X * pixelY) / determinant;
                double worldZ = (basisX.X * pixelY - pixelX * basisX.Y) / determinant;
                double radius = Math.Sqrt(worldX * worldX + worldZ * worldZ);
                if (radius > frame.SupportRadiusWorldBlocks)
                {
                    continue;
                }

                int height = (int)Math.Round(amplitude * Math.Cos(2.0 * Math.PI * radius / wavelength));
                SKColor existing = bitmap.GetPixel(x, y);
                int existingCarrier = (existing.Red - existing.Blue) / 2;
                int carrier = Math.Clamp(existingCarrier + height, -60, 60);
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(128 + carrier),
                        existing.Green,
                        (byte)(128 - carrier),
                        255));
            }
        }
    }

    /// <summary>Creates a spatially varying carrier with exactly linear temporal evolution.</summary>
    private static SKBitmap CreateLinearWindField(int width, int height, int timeStep)
    {
        SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int spatial = ((x / 11 + y / 7) % 9) - 4;
                int temporal = timeStep * (1 + ((x / 37 + y / 29) & 1));
                int carrier = spatial + temporal;
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(128 + carrier),
                        (byte)(24 + timeStep),
                        (byte)(128 - carrier),
                        255));
            }
        }

        return bitmap;
    }

    /// <summary>Creates a spatially varying carrier with exactly quadratic temporal evolution.</summary>
    private static SKBitmap CreateQuadraticWindField(int width, int height, int timeStep)
    {
        SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int spatial = ((x / 11 + y / 7) % 9) - 4;
                int velocity = 1 + ((x / 37 + y / 29) & 1);
                int acceleration = (x / 43 + y / 31) % 3;
                int carrier = spatial
                    + velocity * timeStep
                    + acceleration * timeStep * timeStep;
                int normalCarrier = 24
                    + timeStep
                    + ((x / 53 + y / 47) & 1) * timeStep * timeStep;
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(128 + carrier),
                        (byte)normalCarrier,
                        (byte)(128 - carrier),
                        255));
            }
        }

        return bitmap;
    }

    /// <summary>Saves an effect PNG whose matching before path exists only as a stable log token.</summary>
    private static string SaveField(
        string directory,
        string label,
        int impactX,
        int impactY,
        int amplitude,
        DateTime timestamp)
    {
        string effect = Path.Combine(
            directory,
            $"{timestamp:yyyyMMdd-HHmmssfff}-{label}-vintagertx.png");
        using SKBitmap bitmap = CreateField(128, 128, impactX, impactY, amplitude);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(effect);
        data.SaveTo(stream);
        return effect;
    }

    /// <summary>Formats the exact comparison-save grammar consumed by the runtime validator.</summary>
    private static string SavedPair(string effectPath)
    {
        string before = effectPath.Replace("-vintagertx.png", "-before.png", StringComparison.Ordinal);
        return $"[VintageRTX] Comparison capture saved: {before} and {effectPath}";
    }

    /// <summary>Formats the response-capture line that binds evidence to one callback sequence.</summary>
    private static string SynchronizedCapture(string kind, int sequence) =>
        $"[VintageRTX.Test] Projectile-synchronized capture queued: projectile={kind}, "
        + $"source-sequence={sequence}, energy=2.0000 J, view=LiquidSurfaceField, "
        + $"label=projectile-{kind}-surface-field.";

    /// <summary>Formats authoritative packet wavelength evidence for one callback sequence.</summary>
    private static string WavePacket(
        int sequence,
        double amplitudeMetres,
        double wavelengthMetres) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "[VintageRTX.Test] Projectile surface impact applied: sequence={0}, entity=1, class=Fixture, kind=GenericEntry, world=(0.000,0.000), cell=(1,1)/128x128, mass=0.100 kg, energy=1.0000 J, incident=(0.000,-1.000,0.000) m/s, outgoing=(0.000,-1.000,0.000) m/s. source=server-authoritative. subgrid-packet-sequence={0}, surface=1.000000 J, resolved=0.100000 J, subgrid=0.900000 J, amplitude={1:0.000000} m, wavelength={2:0.000000} m, near=0.100000 J, cavity=0.010000 J, capillary=0.001000 J, splash=0.020000 J, wake=0.069000 J, rendered=0.001000 J, local-splash=0.030000 J, local-amplitude=0.004000 m.",
            sequence,
            amplitudeMetres,
            wavelengthMetres);

    /// <summary>Formats one contact and four unforced liquid-plane controls for a fixture impact.</summary>
    private static string SurfaceFrames(int sequence, string kind, int centerX, int centerY)
    {
        (string Role, int X, int Y)[] frames =
        [
            ("contact", centerX, centerY),
            ("control-1", centerX - 24, centerY - 24),
            ("control-2", centerX + 24, centerY - 24),
            ("control-3", centerX - 24, centerY + 24),
            ("control-4", centerX + 24, centerY + 24)
        ];
        return string.Join(
            '\n',
            frames.Select(frame =>
                $"[VintageRTX.Test] Projectile impact surface frame: sequence={sequence}, projectile={kind}, role={frame.Role}, center=({frame.X:0.00},{frame.Y:0.00}), dscreen-dworld-x=(6.00,0.00), dscreen-dworld-z=(0.00,6.00), support-radius=2.000 m."));
    }
}
