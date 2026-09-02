using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace VintageRTX.Test;

/// <summary>Deterministic structural tests for the positive water-entity reflection gate.</summary>
[TestClass]
public sealed class RuntimeImageValidatorReflectionWitnessTests
{
    /// <summary>Locks the morphology observed in the corrected 1.22.7 real-game body carrier.</summary>
    [TestMethod]
    public void RealRuntimeLocalBodyCarrierRetainsMeasuredComponents()
    {
        string path = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "tests",
            "VintageRTX.Test",
            "Fixtures",
            "entity-mirror-local-body-real.png");
        using SKBitmap bitmap = SKBitmap.Decode(path)
            ?? throw new InvalidDataException($"Could not decode real entity-mirror fixture '{path}'.");

        IReadOnlyList<RuntimeImageValidator.EntityMirrorComponentAssessment> components =
            RuntimeImageValidator.AssessEntityMirrorComponents(bitmap);
        foreach (RuntimeImageValidator.EntityMirrorComponentAssessment component in components)
        {
            Console.WriteLine(
                $"component area={component.Area}, bounds={component.MinimumX},{component.MinimumY}-"
                + $"{component.MaximumX},{component.MaximumY}, size={component.Width}x{component.Height}, "
                + $"centroid={component.CentroidX:0.000}/{component.CentroidY:0.000}, fill={component.FillRatio:0.000}");
        }

        Assert.AreEqual(3, components.Count);
        Assert.AreEqual(2404, components[0].Area);
        Assert.AreEqual(51, components[0].Width);
        Assert.AreEqual(70, components[0].Height);
        Assert.AreEqual(0.498, components[0].CentroidX, 0.001);
        Assert.AreEqual(0.900, components[0].CentroidY, 0.001);
        Assert.AreEqual(1064, components[1].Area);
        Assert.AreEqual(34, components[1].Width);
        Assert.AreEqual(45, components[1].Height);
        Assert.AreEqual(128, components[2].Area);

        RuntimeImageValidator.EntityMirrorLocalBodyAssessment clean =
            RuntimeImageValidator.AssessLocalPlayerWorldBody(bitmap);
        Assert.IsTrue(clean.HasWorldBody);
        Assert.IsFalse(clean.HasFirstPersonOverlay);
        Assert.IsTrue(clean.MeetsGate);
        Assert.AreEqual(2404, clean.WorldBodyArea);

        using SKBitmap contaminated = bitmap.Copy();
        Fill(contaminated, 0, 220, 42, 78, new SKColor(124, 67, 24));
        Fill(contaminated, 38, 242, 30, 20, new SKColor(184, 116, 38));
        RuntimeImageValidator.EntityMirrorLocalBodyAssessment overlay =
            RuntimeImageValidator.AssessLocalPlayerWorldBody(contaminated);
        Assert.IsTrue(overlay.HasWorldBody);
        Assert.IsTrue(overlay.HasFirstPersonOverlay);
        Assert.IsFalse(overlay.MeetsGate);
    }

    /// <summary>
    /// Keeps long-range entity evidence independent from the steep, physically framed local-body
    /// carrier: the validator must never fall back to the wide-view entity-mirror image.
    /// </summary>
    [TestMethod]
    public void LocalBodyGateSelectsOnlyDedicatedEntityMirrorCapture()
    {
        System.Reflection.MethodInfo method = typeof(RuntimeImageValidator).GetMethod(
            "RawEntityMirrorRegex",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RuntimeImageValidator).FullName, "RawEntityMirrorRegex");
        System.Text.RegularExpressions.Regex regex =
            (System.Text.RegularExpressions.Regex)method.Invoke(null, null)!;
        string log = """
            Comparison capture saved: C:\capture\entity-mirror-before.png, C:\capture\entity-mirror-vintagertx.png, and raw pre-final C:\capture\entity-mirror-raw.png
            Comparison capture saved: C:\capture\20260901-local-body-entity-mirror-before.png, C:\capture\20260901-local-body-entity-mirror-vintagertx.png, and raw pre-final C:\capture\20260901-local-body-entity-mirror-raw.png
            """;

        System.Text.RegularExpressions.Match match = regex.Match(log);

        Assert.IsTrue(match.Success);
        Assert.AreEqual(
            @"C:\capture\20260901-local-body-entity-mirror-raw.png",
            match.Groups[1].Value);
        Assert.AreEqual(1, regex.Matches(log).Count);
    }

    /// <summary>Accepts a compact lower-frame local body alongside the two remote witnesses.</summary>
    [TestMethod]
    public void EntityMirrorRequiresLocalWorldBodyBeyondRemoteWitnesses()
    {
        using SKBitmap entityMirror = CreateEntityMirrorCarrier();
        DrawRemoteMirrorWitnesses(entityMirror);
        DrawLocalWorldBody(entityMirror);

        RuntimeImageValidator.EntityMirrorLocalBodyAssessment assessment =
            RuntimeImageValidator.AssessLocalPlayerWorldBody(entityMirror);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsTrue(assessment.HasWorldBody);
        Assert.IsFalse(assessment.HasFirstPersonOverlay);
        Assert.IsTrue(assessment.MeetsGate);
        Assert.IsTrue(assessment.WorldBodyArea >= 1_500);
        Assert.IsTrue(assessment.WorldBodyHeightRatio >= 0.30);
        Assert.IsTrue(assessment.WorldBodyCentroidX is >= 0.40 and <= 0.60);
        Assert.IsTrue(assessment.WorldBodyCentroidY >= 0.75);
    }

    /// <summary>Proves that the two existing remote witnesses cannot impersonate the local body.</summary>
    [TestMethod]
    public void RemoteWitnessesAloneDoNotProveLocalPlayerReflection()
    {
        using SKBitmap entityMirror = CreateEntityMirrorCarrier();
        DrawRemoteMirrorWitnesses(entityMirror);

        RuntimeImageValidator.EntityMirrorLocalBodyAssessment assessment =
            RuntimeImageValidator.AssessLocalPlayerWorldBody(entityMirror);

        Assert.IsFalse(assessment.HasWorldBody);
        Assert.IsFalse(assessment.MeetsGate);
        Assert.AreEqual(0, assessment.WorldBodyArea);
    }

    /// <summary>Rejects the historical large left-edge first-person arm/torch carrier.</summary>
    [TestMethod]
    public void LeftCameraSpaceArmAndTorchInvalidateOtherwiseValidWorldBody()
    {
        using SKBitmap entityMirror = CreateEntityMirrorCarrier();
        DrawRemoteMirrorWitnesses(entityMirror);
        DrawLocalWorldBody(entityMirror);
        Fill(entityMirror, 0, 65, 42, 78, new SKColor(124, 67, 24));
        Fill(entityMirror, 38, 82, 30, 20, new SKColor(184, 116, 38));

        RuntimeImageValidator.EntityMirrorLocalBodyAssessment assessment =
            RuntimeImageValidator.AssessLocalPlayerWorldBody(entityMirror);

        Assert.IsTrue(assessment.HasWorldBody);
        Assert.IsTrue(assessment.HasFirstPersonOverlay);
        Assert.IsFalse(assessment.MeetsGate);
        Assert.IsTrue(assessment.FirstPersonOverlayArea >= 3_000);
    }

    /// <summary>Rejects isolated readback noise and a small reflected dropped item as a body.</summary>
    [TestMethod]
    public void BottomNoiseAndDroppedItemCannotImpersonateWorldBody()
    {
        using SKBitmap entityMirror = CreateEntityMirrorCarrier();
        DrawRemoteMirrorWitnesses(entityMirror);
        Fill(entityMirror, 154, 146, 12, 9, new SKColor(84, 78, 61));
        for (int index = 0; index < 20; index++)
        {
            entityMirror.SetPixel(
                90 + index * 7,
                135 + index % 17,
                new SKColor(18, 20, 17));
        }

        RuntimeImageValidator.EntityMirrorLocalBodyAssessment assessment =
            RuntimeImageValidator.AssessLocalPlayerWorldBody(entityMirror);

        Assert.IsFalse(assessment.HasWorldBody);
        Assert.IsFalse(assessment.MeetsGate);
    }

    /// <summary>Rejects thumbnails before applying normalized component thresholds.</summary>
    [TestMethod]
    public void TinyEntityMirrorCannotProduceWorldBodyEvidence()
    {
        using SKBitmap entityMirror = new(32, 32);
        entityMirror.Erase(SKColors.Black);
        Fill(entityMirror, 8, 8, 16, 24, SKColors.White);

        RuntimeImageValidator.EntityMirrorLocalBodyAssessment assessment =
            RuntimeImageValidator.AssessLocalPlayerWorldBody(entityMirror);

        Assert.IsFalse(assessment.Compatible);
        Assert.IsFalse(assessment.MeetsGate);
    }

    /// <summary>Accepts two central source silhouettes followed by two ordered reflected components.</summary>
    [TestMethod]
    public void CentralHumanoidAndItemReflectionsPassInPhysicalOrder()
    {
        using SKBitmap source = CreateSource();
        using SKBitmap reflection = CreateReflectionCarrier();
        Fill(reflection, 96, 65, 9, 1, new SKColor(55, 45, 30));
        Fill(reflection, 98, 68, 5, 6, new SKColor(70, 66, 57));

        RuntimeImageValidator.ReflectionWitnessAssessment assessment =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflection);

        Assert.IsTrue(assessment.Compatible);
        Assert.IsTrue(assessment.MeetsProvisionalGate);
        Assert.IsTrue(assessment.HumanoidReflectionArea >= 8);
        Assert.IsTrue(assessment.ItemReflectionArea >= 30);
        Assert.IsTrue(
            assessment.ItemReflectionCentroidY
                > assessment.HumanoidReflectionCentroidY);
    }

    /// <summary>Rejects a clean source whose witnesses never reach the reflected-water bands.</summary>
    [TestMethod]
    public void DirectSourceWithoutReflectedComponentsFails()
    {
        using SKBitmap source = CreateSource();
        using SKBitmap reflection = CreateReflectionCarrier();

        RuntimeImageValidator.ReflectionWitnessAssessment assessment =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflection);

        Assert.IsFalse(assessment.MeetsProvisionalGate);
        Assert.AreEqual(0, assessment.HumanoidReflectionArea);
        Assert.AreEqual(0, assessment.ItemReflectionArea);
    }

    /// <summary>Rejects silhouettes retained at their direct/immersed row instead of mirrored below the interface.</summary>
    [TestMethod]
    public void DirectOrSubmergedSilhouettesDoNotCountAsReflection()
    {
        using SKBitmap source = CreateSource();
        using SKBitmap reflection = CreateReflectionCarrier();
        Fill(reflection, 96, 46, 9, 12, new SKColor(55, 45, 30));
        Fill(reflection, 98, 59, 5, 5, new SKColor(70, 66, 57));

        RuntimeImageValidator.ReflectionWitnessAssessment assessment =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflection);

        Assert.IsFalse(assessment.MeetsProvisionalGate);
        Assert.AreEqual(0, assessment.ItemReflectionArea);
    }

    /// <summary>
    /// Excludes the last direct row at 527 while accepting a distinct reflected component whose
    /// first pixel is row 528, the ceiling of 52.3% for the 1009-pixel runtime capture.
    /// </summary>
    [TestMethod]
    public void ReflectionBoundaryRejectsRow527AndAcceptsRow528()
    {
        using SKBitmap source = CreateRuntimeHeightSource();
        using SKBitmap directTail = CreateReflectionCarrier(320, 1009);
        Fill(directTail, 156, 526, 8, 2, new SKColor(55, 45, 30));

        RuntimeImageValidator.ReflectionWitnessAssessment rejected =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, directTail);

        Assert.AreEqual(0, rejected.HumanoidReflectionArea);
        Assert.IsFalse(rejected.MeetsProvisionalGate);

        using SKBitmap reflected = CreateReflectionCarrier(320, 1009);
        Fill(reflected, 156, 528, 8, 3, new SKColor(55, 45, 30));
        Fill(reflected, 158, 570, 5, 4, new SKColor(70, 66, 57));

        RuntimeImageValidator.ReflectionWitnessAssessment accepted =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflected);

        Assert.IsTrue(accepted.MeetsProvisionalGate);
        Assert.IsTrue(accepted.HumanoidReflectionCentroidY >= 528.0 / 1009.0);
        Assert.IsTrue(accepted.ItemReflectionCentroidY >= 0.545);
    }

    /// <summary>Rejects one four-connected vertical trail even when it crosses both reflection bands.</summary>
    [TestMethod]
    public void OneConnectedTrailCannotRepresentBothWitnesses()
    {
        using SKBitmap source = CreateSource();
        using SKBitmap reflection = CreateReflectionCarrier();
        Fill(reflection, 98, 65, 5, 22, new SKColor(55, 45, 30));

        RuntimeImageValidator.ReflectionWitnessAssessment assessment =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflection);

        Assert.IsFalse(assessment.MeetsProvisionalGate);
        Assert.IsTrue(assessment.HumanoidReflectionArea > 0);
        Assert.IsTrue(assessment.ItemReflectionArea > 0);
    }

    /// <summary>Rejects a large first-person overlay because only the central water corridor is eligible.</summary>
    [TestMethod]
    public void LeftFirstPersonOverlayCannotSatisfyEntityGate()
    {
        using SKBitmap source = CreateSource();
        using SKBitmap reflection = CreateReflectionCarrier();
        Fill(reflection, 0, 55, 58, 60, new SKColor(20, 12, 8));

        RuntimeImageValidator.ReflectionWitnessAssessment assessment =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflection);

        Assert.IsFalse(assessment.MeetsProvisionalGate);
        Assert.AreEqual(0, assessment.HumanoidReflectionArea);
        Assert.AreEqual(0, assessment.ItemReflectionArea);
    }

    /// <summary>Allows bounded wave displacement without relaxing the required vertical ordering.</summary>
    [TestMethod]
    public void BoundedWaveDisplacementRetainsBothWitnesses()
    {
        using SKBitmap source = CreateSource();
        using SKBitmap reflection = CreateReflectionCarrier();
        Fill(reflection, 94, 65, 8, 1, new SKColor(60, 48, 33));
        Fill(reflection, 101, 69, 4, 5, new SKColor(72, 67, 58));

        RuntimeImageValidator.ReflectionWitnessAssessment assessment =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflection);

        Assert.IsTrue(assessment.MeetsProvisionalGate);
    }

    /// <summary>Returns incompatible evidence instead of comparing differently sized captures.</summary>
    [TestMethod]
    public void IncompatibleCaptureDimensionsAreRejected()
    {
        using SKBitmap source = CreateSource();
        using SKBitmap reflection = new(199, 120);

        RuntimeImageValidator.ReflectionWitnessAssessment assessment =
            RuntimeImageValidator.AssessCentralReflectionWitnesses(source, reflection);

        Assert.IsFalse(assessment.Compatible);
        Assert.IsFalse(assessment.MeetsProvisionalGate);
    }

    /// <summary>Creates the clean-source fixture with an alpha-shaped humanoid and a small opaque item.</summary>
    private static SKBitmap CreateSource()
    {
        SKBitmap bitmap = new(200, 120);
        bitmap.Erase(new SKColor(16, 25, 24));
        Fill(bitmap, 96, 46, 9, 13, new SKColor(82, 68, 43));
        // A one-pixel waist gap retains alpha-tested shape structure without
        // changing the source-presence criterion into a filled rectangle test.
        Fill(bitmap, 96, 51, 2, 2, new SKColor(16, 25, 24));
        Fill(bitmap, 98, 59, 5, 5, new SKColor(78, 73, 60));
        return bitmap;
    }

    /// <summary>Creates the 1009-row source fixture whose direct granite ends exactly at row 522.</summary>
    private static SKBitmap CreateRuntimeHeightSource()
    {
        SKBitmap bitmap = new(320, 1009);
        bitmap.Erase(new SKColor(16, 25, 24));
        Fill(bitmap, 156, 390, 8, 109, new SKColor(82, 68, 43));
        Fill(bitmap, 156, 442, 2, 4, new SKColor(16, 25, 24));
        Fill(bitmap, 158, 499, 5, 24, new SKColor(78, 73, 60));
        return bitmap;
    }

    /// <summary>Creates a uniform bright reflection diagnostic representative of open water.</summary>
    private static SKBitmap CreateReflectionCarrier()
    {
        return CreateReflectionCarrier(200, 120);
    }

    /// <summary>Creates a uniformly bright reflection diagnostic with explicit dimensions.</summary>
    /// <param name="width">Positive bitmap width.</param>
    /// <param name="height">Positive bitmap height.</param>
    /// <returns>Bright carrier used to isolate locally darker reflection components.</returns>
    private static SKBitmap CreateReflectionCarrier(int width, int height)
    {
        SKBitmap bitmap = new(width, height);
        bitmap.Erase(new SKColor(205, 216, 232));
        return bitmap;
    }

    /// <summary>Creates a native half-resolution-style black entity-only carrier.</summary>
    private static SKBitmap CreateEntityMirrorCarrier()
    {
        SKBitmap bitmap = new(320, 180);
        bitmap.Erase(SKColors.Black);
        return bitmap;
    }

    /// <summary>Adds the central strawdummy and dropped-item silhouettes from the runtime scenario.</summary>
    private static void DrawRemoteMirrorWitnesses(SKBitmap bitmap)
    {
        Fill(bitmap, 150, 70, 18, 34, new SKColor(74, 61, 39));
        Fill(bitmap, 155, 128, 10, 9, new SKColor(82, 77, 62));
    }

    /// <summary>Adds connected head, torso, arms, and legs in the near-camera reflection band.</summary>
    private static void DrawLocalWorldBody(SKBitmap bitmap)
    {
        SKColor cloth = new(68, 82, 91);
        Fill(bitmap, 146, 116, 28, 16, new SKColor(108, 83, 61));
        Fill(bitmap, 138, 132, 44, 22, cloth);
        Fill(bitmap, 144, 154, 12, 24, cloth);
        Fill(bitmap, 164, 154, 12, 24, cloth);
    }

    /// <summary>Fills one clipped fixture rectangle without invoking a renderer.</summary>
    private static void Fill(
        SKBitmap bitmap,
        int x,
        int y,
        int width,
        int height,
        SKColor color)
    {
        for (int row = Math.Max(0, y); row < Math.Min(bitmap.Height, y + height); row++)
        {
            for (int column = Math.Max(0, x); column < Math.Min(bitmap.Width, x + width); column++)
            {
                bitmap.SetPixel(column, row, color);
            }
        }
    }
}
