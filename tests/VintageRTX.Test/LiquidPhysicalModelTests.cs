using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Verifies unit-safe physical equations independently of the renderer and game runtime.</summary>
[TestClass]
public sealed class LiquidPhysicalModelTests
{
    /// <summary>Checks exact normal-incidence dielectric reflection against the analytic result.</summary>
    [TestMethod]
    public void FresnelNormalIncidenceMatchesAnalyticDielectricResult()
    {
        double expected = Math.Pow((1.0 - 1.333) / (1.0 + 1.333), 2.0);
        double actual = LiquidPhysicalModel.FresnelReflectance(1.0, 1.0, 1.333);

        Assert.AreEqual(expected, actual, 1e-12);
        Assert.IsTrue(actual > 0.020 && actual < 0.021);
    }

    /// <summary>Checks the critical-angle branch for a ray leaving water for air.</summary>
    [TestMethod]
    public void FresnelReturnsTotalInternalReflectionBeyondCriticalAngle()
    {
        double reflectance = LiquidPhysicalModel.FresnelReflectance(0.5, 1.333, 1.0);

        Assert.AreEqual(1.0, reflectance, 0.0);
    }

    /// <summary>Checks extinction per metre and the one-block-to-one-metre calibration convention.</summary>
    [TestMethod]
    public void BeerLambertUsesMetresAndNapierianExtinction()
    {
        double pathMetres = 4.0 * LiquidPhysicalModel.MetresPerWorldBlock;
        double transmittance = LiquidPhysicalModel.BeerLambertTransmittance(0.25, pathMetres);

        Assert.AreEqual(Math.Exp(-1.0), transmittance, 1e-12);
    }

    /// <summary>Checks that impact displacement follows mass, speed, density, area, and efficiency.</summary>
    [TestMethod]
    public void ImpactEnergyProducesDimensionallyConsistentDisplacement()
    {
        double energy = LiquidPhysicalModel.KineticEnergyJoules(25.0, 4.0);
        double displacement = LiquidPhysicalModel.ImpactDisplacementMetres(
            energy,
            0.02,
            998.2,
            0.25);
        double doubledSpeed = LiquidPhysicalModel.ImpactDisplacementMetres(
            LiquidPhysicalModel.KineticEnergyJoules(25.0, 8.0),
            0.02,
            998.2,
            0.25);

        Assert.AreEqual(200.0, energy, 1e-12);
        Assert.IsTrue(displacement > 0.05 && displacement < 0.06);
        Assert.AreEqual(displacement * 2.0, doubledSpeed, 1e-12);
    }

    /// <summary>Checks the gravity-capillary dispersion relation and real water material inputs.</summary>
    [TestMethod]
    public void WaterPhaseSpeedComesFromGravityDensityAndSurfaceTension()
    {
        double speed = LiquidPhysicalModel.DeepWaterPhaseSpeedMetresPerSecond(
            1.0,
            998.2,
            0.07275);

        Assert.IsTrue(speed > 1.24 && speed < 1.27);
    }

    /// <summary>
    /// Checks finite-depth dispersion and verifies that a localized deep-water packet advances at
    /// group velocity rather than the roughly two-times-faster gravity-wave crest velocity.
    /// </summary>
    [TestMethod]
    public void LocalizedWavePacketUsesFiniteDepthGroupVelocity()
    {
        const double wavelengthMetres = 3.0;
        const double densityKilogramsPerCubicMetre = 998.2;
        const double surfaceTensionNewtonsPerMetre = 0.07275;
        double deepPhase = LiquidPhysicalModel.GravityCapillaryPhaseVelocityMetresPerSecond(
            wavelengthMetres,
            double.PositiveInfinity,
            densityKilogramsPerCubicMetre,
            surfaceTensionNewtonsPerMetre);
        double deepGroup = LiquidPhysicalModel.GravityCapillaryGroupVelocityMetresPerSecond(
            wavelengthMetres,
            double.PositiveInfinity,
            densityKilogramsPerCubicMetre,
            surfaceTensionNewtonsPerMetre);
        double shallowPhase = LiquidPhysicalModel.GravityCapillaryPhaseVelocityMetresPerSecond(
            wavelengthMetres,
            0.25,
            densityKilogramsPerCubicMetre,
            surfaceTensionNewtonsPerMetre);
        double shallowGroup = LiquidPhysicalModel.GravityCapillaryGroupVelocityMetresPerSecond(
            wavelengthMetres,
            0.25,
            densityKilogramsPerCubicMetre,
            surfaceTensionNewtonsPerMetre);

        Assert.IsTrue(deepPhase > 2.15 && deepPhase < 2.18);
        Assert.IsTrue(deepGroup > 1.07 && deepGroup < 1.10);
        Assert.IsTrue(deepGroup < deepPhase * 0.51);
        Assert.IsTrue(shallowPhase > 1.48 && shallowPhase < 1.52);
        Assert.IsTrue(shallowGroup > 1.36 && shallowGroup < 1.40);
        Assert.IsTrue(shallowGroup > deepGroup);
    }

    /// <summary>Checks that physical viscosity yields a frame-rate-independent exponential rate.</summary>
    [TestMethod]
    public void ViscousDampingIsExpressedPerSecond()
    {
        double water = LiquidPhysicalModel.ViscousAmplitudeDampingPerSecond(
            0.1,
            998.2,
            0.001002);
        double honey = LiquidPhysicalModel.ViscousAmplitudeDampingPerSecond(
            0.1,
            1496.0,
            7.85);

        Assert.IsTrue(water > 0.007 && water < 0.009);
        Assert.IsTrue(honey > water * 5000.0);
    }

    /// <summary>Checks the measured Cox-Munk wind term and explicit engine-to-SI calibration.</summary>
    [TestMethod]
    public void WindSlopeUsesMeasuredMetresPerSecondRelation()
    {
        double calibratedWind = 0.5 * LiquidPhysicalModel.WindMetresPerSecondPerEngineUnit;
        double meanSquareSlope = LiquidPhysicalModel.CleanWaterWindDrivenMeanSquareSlope(
            calibratedWind);

        Assert.AreEqual(4.0, calibratedWind, 0.0);
        Assert.AreEqual(0.02048, meanSquareSlope, 1e-12);
        Assert.AreEqual(0.0, LiquidPhysicalModel.CleanWaterWindDrivenMeanSquareSlope(0.0), 0.0);
    }

    /// <summary>Checks thermal emission grows rapidly with basaltic melt temperature.</summary>
    [TestMethod]
    public void PlanckRadianceRespondsToTemperatureAndEmissivity()
    {
        double cool = LiquidPhysicalModel.PlanckSpectralRadianceWattsPerSteradianCubicMetre(
            650e-9,
            1200.0,
            0.8);
        double hot = LiquidPhysicalModel.PlanckSpectralRadianceWattsPerSteradianCubicMetre(
            650e-9,
            1473.0,
            0.8);
        double halfEmissivity = LiquidPhysicalModel.PlanckSpectralRadianceWattsPerSteradianCubicMetre(
            650e-9,
            1473.0,
            0.4);

        Assert.IsTrue(hot > cool * 20.0);
        Assert.AreEqual(hot * 0.5, halfEmissivity, hot * 1e-12);
    }

    /// <summary>Checks that invalid physical quantities fail instead of being silently clamped.</summary>
    [TestMethod]
    public void InvalidPhysicalInputsAreRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.FresnelReflectance(-0.1, 1.0, 1.333));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.BeerLambertTransmittance(-1.0, 1.0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.KineticEnergyJoules(0.0, 1.0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.ImpactDisplacementMetres(1.0, 1.1, 1000.0, 1.0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.DeepWaterPhaseSpeedMetresPerSecond(0.0, 1000.0, 0.07));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.GravityCapillaryPhaseVelocityMetresPerSecond(
                1.0,
                0.0,
                1000.0,
                0.07));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.GravityCapillaryGroupVelocityMetresPerSecond(
                1.0,
                double.NaN,
                1000.0,
                0.07));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.ViscousAmplitudeDampingPerSecond(1.0, 1000.0, -0.1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.CleanWaterWindDrivenMeanSquareSlope(double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiquidPhysicalModel.PlanckSpectralRadianceWattsPerSteradianCubicMetre(
                650e-9,
                1473.0,
                double.NaN));
    }
}
