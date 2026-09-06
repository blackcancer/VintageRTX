using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Test;

/// <summary>Exercises receiver continuity and physically combined PBR-normal evidence.</summary>
[TestClass]
public sealed class RuntimeImageValidatorPbrNormalTests
{
    /// <summary>Material or depth boundaries must not inflate the excessive-angle denominator.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("PBR")]
    public void ReceiverDiscontinuitiesAreExcludedFromTheExcessiveDenominator()
    {
        RuntimeImageValidator.PbrNormalNeighborhoodSample center = Sample(0.0, 40, 80, 220, 100);
        RuntimeImageValidator.PbrNormalNeighborhoodSample[] materialBoundary = CoherentNeighbors();
        materialBoundary[0] = Sample(2.0, 45, 80, 220, 100);
        RuntimeImageValidator.PbrNormalNeighborhoodAssessment materialAssessment =
            RuntimeImageValidator.AssessPbrNormalNeighborhood(in center, materialBoundary);
        Assert.IsFalse(materialAssessment.CountsTowardExcessiveDenominator);
        Assert.IsFalse(materialAssessment.IsExcessive);

        RuntimeImageValidator.PbrNormalNeighborhoodSample[] depthBoundary = CoherentNeighbors();
        depthBoundary[1] = Sample(-2.0, 40, 80, 220, 102);
        RuntimeImageValidator.PbrNormalNeighborhoodAssessment depthAssessment =
            RuntimeImageValidator.AssessPbrNormalNeighborhood(in center, depthBoundary);
        Assert.IsFalse(depthAssessment.CountsTowardExcessiveDenominator);
        Assert.IsFalse(depthAssessment.IsExcessive);
    }

    /// <summary>A coherent tangent-space relief contributes both accepted and responsive evidence.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("PBR")]
    public void CoherentTangentReliefCountsAsResponsiveNormalMapEvidence()
    {
        RuntimeImageValidator.PbrNormalNeighborhoodSample center = Sample(0.0, 40, 80, 220, 100);
        RuntimeImageValidator.PbrNormalNeighborhoodAssessment assessment =
            RuntimeImageValidator.AssessPbrNormalNeighborhood(in center, CoherentNeighbors());

        Assert.IsTrue(assessment.CountsTowardExcessiveDenominator);
        Assert.IsFalse(assessment.IsExcessive);
        Assert.IsTrue(assessment.IsResponsive);
        Assert.IsTrue(assessment.AngularRmsDegrees is > 1.0 and < 3.0);
    }

    /// <summary>A single axial jump is excluded because the 8-bit depth image cannot prove continuity.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("PBR")]
    public void SingleAxialNormalJumpIsExcludedAsAnUnresolvedGeometryEdge()
    {
        RuntimeImageValidator.PbrNormalNeighborhoodSample center = Sample(0.0, 40, 80, 220, 100);
        RuntimeImageValidator.PbrNormalNeighborhoodSample[] neighbors = CoherentNeighbors();
        neighbors[2] = Sample(40.0, 40, 80, 220, 100);

        RuntimeImageValidator.PbrNormalNeighborhoodAssessment assessment =
            RuntimeImageValidator.AssessPbrNormalNeighborhood(in center, neighbors);

        Assert.IsFalse(assessment.CountsTowardExcessiveDenominator);
        Assert.IsFalse(assessment.IsExcessive);
        Assert.IsFalse(assessment.IsResponsive);
        Assert.AreEqual(0.0, assessment.AngularRmsDegrees);
    }

    /// <summary>Multiple large disagreements remain a real overdriven tangent-field failure.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("PBR")]
    public void MultiDirectionNormalSpikeAboveThirtyFiveDegreesIsExcessive()
    {
        RuntimeImageValidator.PbrNormalNeighborhoodSample center = Sample(0.0, 40, 80, 220, 100);
        RuntimeImageValidator.PbrNormalNeighborhoodSample[] neighbors = CoherentNeighbors();
        neighbors[0] = Sample(40.0, 40, 80, 220, 100);
        neighbors[2] = Sample(-40.0, 40, 80, 220, 100);

        RuntimeImageValidator.PbrNormalNeighborhoodAssessment assessment =
            RuntimeImageValidator.AssessPbrNormalNeighborhood(in center, neighbors);

        Assert.IsTrue(assessment.CountsTowardExcessiveDenominator);
        Assert.IsTrue(assessment.IsExcessive);
        Assert.IsFalse(assessment.IsResponsive);
        Assert.IsTrue(assessment.AngularRmsDegrees > 28.0);
    }

    /// <summary>The cave result passes only through strong combined angular-energy evidence.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("PBR")]
    public void AppliedNormalGateCombinesAmplitudeCoverageAndGlobalAngularEnergy()
    {
        Assert.IsTrue(RuntimeImageValidator.HasAppliedPbrNormalResponse(0.70, 0.20, 0.35));
        Assert.IsTrue(RuntimeImageValidator.HasAppliedPbrNormalResponse(1.271, 0.17, 0.42));
        Assert.IsFalse(RuntimeImageValidator.HasAppliedPbrNormalResponse(0.65, 0.17, 0.42));
        Assert.IsFalse(RuntimeImageValidator.HasAppliedPbrNormalResponse(1.271, 0.05, 0.50));
        Assert.IsFalse(RuntimeImageValidator.HasAppliedPbrNormalResponse(1.271, 0.17, 0.39));
        Assert.IsFalse(RuntimeImageValidator.HasAppliedPbrNormalResponse(double.NaN, 0.20, 0.50));
        Assert.IsFalse(RuntimeImageValidator.HasAppliedPbrNormalResponse(1.0, -0.01, 0.50));
        Assert.IsFalse(RuntimeImageValidator.HasAppliedPbrNormalResponse(1.0, 1.01, 0.50));
        Assert.IsFalse(RuntimeImageValidator.HasAppliedPbrNormalResponse(1.0, 0.20, double.NaN));
    }

    /// <summary>Malformed crosses and invalid decoded normals are rejected before classification.</summary>
    [TestMethod]
    [TestCategory("Runtime")]
    [TestCategory("PBR")]
    public void InvalidNormalNeighborhoodsAreExcludedOrRejected()
    {
        RuntimeImageValidator.PbrNormalNeighborhoodSample center = Sample(0.0, 40, 80, 220, 100);
        Assert.ThrowsException<ArgumentException>(() =>
            RuntimeImageValidator.AssessPbrNormalNeighborhood(
                center,
                CoherentNeighbors()[..3]));

        RuntimeImageValidator.PbrNormalNeighborhoodSample uncovered =
            new(Vector3.UnitZ, 40, 80, 127, 100);
        Assert.IsFalse(RuntimeImageValidator.AssessPbrNormalNeighborhood(
            in uncovered,
            CoherentNeighbors()).CountsTowardExcessiveDenominator);

        RuntimeImageValidator.PbrNormalNeighborhoodSample invalidCenter =
            new(Vector3.Zero, 40, 80, 220, 100);
        Assert.IsFalse(RuntimeImageValidator.AssessPbrNormalNeighborhood(
            in invalidCenter,
            CoherentNeighbors()).CountsTowardExcessiveDenominator);

        RuntimeImageValidator.PbrNormalNeighborhoodSample[] invalidNeighbor = CoherentNeighbors();
        invalidNeighbor[3] = new RuntimeImageValidator.PbrNormalNeighborhoodSample(
            new Vector3(float.NaN, 0.0f, 1.0f),
            40,
            80,
            220,
            100);
        Assert.IsFalse(RuntimeImageValidator.AssessPbrNormalNeighborhood(
            in center,
            invalidNeighbor).CountsTowardExcessiveDenominator);
    }

    /// <summary>Builds four small, same-receiver tangent perturbations around a flat center.</summary>
    /// <returns>Four valid axial neighbor samples.</returns>
    private static RuntimeImageValidator.PbrNormalNeighborhoodSample[] CoherentNeighbors()
    {
        return
        [
            Sample(1.5, 41, 80, 220, 100),
            Sample(-1.5, 40, 81, 220, 101),
            Sample(2.0, 39, 80, 221, 100),
            Sample(-2.0, 40, 79, 219, 99)
        ];
    }

    /// <summary>Creates a unit normal rotated tangentially around world Y.</summary>
    /// <param name="angleDegrees">Signed angular offset from world Z.</param>
    /// <param name="materialRed">Red material-mask channel.</param>
    /// <param name="materialGreen">Green material-mask channel.</param>
    /// <param name="materialBlue">Blue authored coverage channel.</param>
    /// <param name="depth">Quantized depth channel.</param>
    /// <returns>A deterministic neighborhood sample.</returns>
    private static RuntimeImageValidator.PbrNormalNeighborhoodSample Sample(
        double angleDegrees,
        byte materialRed,
        byte materialGreen,
        byte materialBlue,
        byte depth)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return new RuntimeImageValidator.PbrNormalNeighborhoodSample(
            new Vector3((float)Math.Sin(radians), 0.0f, (float)Math.Cos(radians)),
            materialRed,
            materialGreen,
            materialBlue,
            depth);
    }
}
