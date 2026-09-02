namespace VintageRTX.Rendering;

/// <summary>
/// Unit-safe equations shared by liquid optics and free-surface dynamics. VintageRTX uses the
/// explicit calibration convention one Vintage Story world block equals one metre; this is a
/// renderer convention, not an engine guarantee. Every argument and result is therefore in SI.
/// </summary>
internal static class LiquidPhysicalModel
{
    /// <summary>Renderer calibration converting Vintage Story world blocks to metres.</summary>
    public const double MetresPerWorldBlock = 1.0;

    /// <summary>
    /// Renderer calibration converting the dimensionless public GetWindSpeedAt vector to m/s.
    /// The Vintage Story API publishes no SI unit for that vector, so this is deliberately an
    /// exposed calibration constant rather than a claimed engine measurement.
    /// </summary>
    public const double WindMetresPerSecondPerEngineUnit = 8.0;

    /// <summary>Standard gravitational acceleration in metres per second squared.</summary>
    public const double StandardGravityMetresPerSecondSquared = 9.80665;

    /// <summary>Planck constant in joule seconds.</summary>
    private const double PlanckConstantJouleSeconds = 6.62607015e-34;

    /// <summary>Speed of light in vacuum in metres per second.</summary>
    private const double SpeedOfLightMetresPerSecond = 299_792_458.0;

    /// <summary>Boltzmann constant in joules per kelvin.</summary>
    private const double BoltzmannConstantJoulesPerKelvin = 1.380649e-23;

    /// <summary>
    /// Computes exact unpolarized dielectric Fresnel reflectance, including total internal
    /// reflection. Indices of refraction are relative to vacuum and the incident cosine is the
    /// absolute angle between the incident ray and interface normal.
    /// </summary>
    /// <param name="incidentCosine">Incident cosine in the inclusive interval 0..1.</param>
    /// <param name="incidentIndex">Positive refractive index of the incident medium.</param>
    /// <param name="transmittedIndex">Positive refractive index of the transmitted medium.</param>
    /// <returns>Unpolarized energy reflectance in the inclusive interval 0..1.</returns>
    public static double FresnelReflectance(
        double incidentCosine,
        double incidentIndex,
        double transmittedIndex)
    {
        RequireFinitePositive(incidentIndex, nameof(incidentIndex));
        RequireFinitePositive(transmittedIndex, nameof(transmittedIndex));
        if (!double.IsFinite(incidentCosine) || incidentCosine < 0.0 || incidentCosine > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(incidentCosine));
        }

        double incidentSineSquared = Math.Max(0.0, 1.0 - incidentCosine * incidentCosine);
        double indexRatio = incidentIndex / transmittedIndex;
        double transmittedSineSquared = indexRatio * indexRatio * incidentSineSquared;
        if (transmittedSineSquared >= 1.0)
        {
            return 1.0;
        }

        double transmittedCosine = Math.Sqrt(1.0 - transmittedSineSquared);
        double perpendicularNumerator = incidentIndex * incidentCosine
            - transmittedIndex * transmittedCosine;
        double perpendicularDenominator = incidentIndex * incidentCosine
            + transmittedIndex * transmittedCosine;
        double parallelNumerator = transmittedIndex * incidentCosine
            - incidentIndex * transmittedCosine;
        double parallelDenominator = transmittedIndex * incidentCosine
            + incidentIndex * transmittedCosine;
        double perpendicular = perpendicularNumerator / perpendicularDenominator;
        double parallel = parallelNumerator / parallelDenominator;
        return Math.Clamp(
            0.5 * (perpendicular * perpendicular + parallel * parallel),
            0.0,
            1.0);
    }

    /// <summary>Applies Beer-Lambert attenuation using a Napierian extinction coefficient.</summary>
    /// <param name="extinctionPerMetre">Non-negative absorption plus out-scattering coefficient in m^-1.</param>
    /// <param name="pathLengthMetres">Non-negative optical path length in metres.</param>
    /// <returns>Surviving radiance fraction in 0..1.</returns>
    public static double BeerLambertTransmittance(
        double extinctionPerMetre,
        double pathLengthMetres)
    {
        RequireFiniteNonNegative(extinctionPerMetre, nameof(extinctionPerMetre));
        RequireFiniteNonNegative(pathLengthMetres, nameof(pathLengthMetres));
        return Math.Exp(-extinctionPerMetre * pathLengthMetres);
    }

    /// <summary>Computes translational kinetic energy available at a surface impact.</summary>
    /// <param name="massKilograms">Positive impacting mass in kilograms.</param>
    /// <param name="speedMetresPerSecond">Non-negative impact speed in metres per second.</param>
    /// <returns>Kinetic energy in joules.</returns>
    public static double KineticEnergyJoules(
        double massKilograms,
        double speedMetresPerSecond)
    {
        RequireFinitePositive(massKilograms, nameof(massKilograms));
        RequireFiniteNonNegative(speedMetresPerSecond, nameof(speedMetresPerSecond));
        return 0.5 * massKilograms * speedMetresPerSecond * speedMetresPerSecond;
    }

    /// <summary>
    /// Converts coupled impact energy to a small-amplitude gravity-wave displacement by equating
    /// it with the potential energy 0.5*rho*g*A^2*S over an effective disturbed area.
    /// </summary>
    /// <param name="kineticEnergyJoules">Non-negative kinetic energy in joules.</param>
    /// <param name="couplingEfficiency">Fraction of impact energy entering the resolved wave field, 0..1.</param>
    /// <param name="densityKilogramsPerCubicMetre">Positive liquid density in kg/m^3.</param>
    /// <param name="effectiveAreaSquareMetres">Positive disturbed surface area in m^2.</param>
    /// <param name="gravityMetresPerSecondSquared">Positive gravitational acceleration in m/s^2.</param>
    /// <returns>Equivalent peak displacement amplitude in metres.</returns>
    public static double ImpactDisplacementMetres(
        double kineticEnergyJoules,
        double couplingEfficiency,
        double densityKilogramsPerCubicMetre,
        double effectiveAreaSquareMetres,
        double gravityMetresPerSecondSquared = StandardGravityMetresPerSecondSquared)
    {
        RequireFiniteNonNegative(kineticEnergyJoules, nameof(kineticEnergyJoules));
        if (!double.IsFinite(couplingEfficiency)
            || couplingEfficiency < 0.0
            || couplingEfficiency > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(couplingEfficiency));
        }

        RequireFinitePositive(
            densityKilogramsPerCubicMetre,
            nameof(densityKilogramsPerCubicMetre));
        RequireFinitePositive(effectiveAreaSquareMetres, nameof(effectiveAreaSquareMetres));
        RequireFinitePositive(
            gravityMetresPerSecondSquared,
            nameof(gravityMetresPerSecondSquared));
        return Math.Sqrt(
            2.0 * couplingEfficiency * kineticEnergyJoules
            / (densityKilogramsPerCubicMetre
                * gravityMetresPerSecondSquared
                * effectiveAreaSquareMetres));
    }

    /// <summary>
    /// Computes deep-water gravity-capillary phase speed from wavelength, density, and surface
    /// tension: c=sqrt(g/k + sigma*k/rho), where k=2*pi/lambda.
    /// </summary>
    /// <param name="wavelengthMetres">Positive wavelength in metres.</param>
    /// <param name="densityKilogramsPerCubicMetre">Positive liquid density in kg/m^3.</param>
    /// <param name="surfaceTensionNewtonsPerMetre">Non-negative surface tension in N/m.</param>
    /// <param name="gravityMetresPerSecondSquared">Positive gravitational acceleration in m/s^2.</param>
    /// <returns>Phase speed in metres per second.</returns>
    public static double DeepWaterPhaseSpeedMetresPerSecond(
        double wavelengthMetres,
        double densityKilogramsPerCubicMetre,
        double surfaceTensionNewtonsPerMetre,
        double gravityMetresPerSecondSquared = StandardGravityMetresPerSecondSquared)
    {
        return GravityCapillaryPhaseVelocityMetresPerSecond(
            wavelengthMetres,
            double.PositiveInfinity,
            densityKilogramsPerCubicMetre,
            surfaceTensionNewtonsPerMetre,
            gravityMetresPerSecondSquared);
    }

    /// <summary>
    /// Computes finite-depth gravity-capillary phase velocity from
    /// omega^2=(g*k+(sigma/rho)*k^3)*tanh(k*h), with c_p=omega/k.
    /// </summary>
    /// <param name="wavelengthMetres">Positive wavelength in metres.</param>
    /// <param name="depthMetres">Positive liquid depth in metres, or positive infinity for deep water.</param>
    /// <param name="densityKilogramsPerCubicMetre">Positive liquid density in kg/m^3.</param>
    /// <param name="surfaceTensionNewtonsPerMetre">Non-negative surface tension in N/m.</param>
    /// <param name="gravityMetresPerSecondSquared">Positive gravitational acceleration in m/s^2.</param>
    /// <returns>Finite positive crest phase velocity in metres per second.</returns>
    public static double GravityCapillaryPhaseVelocityMetresPerSecond(
        double wavelengthMetres,
        double depthMetres,
        double densityKilogramsPerCubicMetre,
        double surfaceTensionNewtonsPerMetre,
        double gravityMetresPerSecondSquared = StandardGravityMetresPerSecondSquared)
    {
        RequireFinitePositive(wavelengthMetres, nameof(wavelengthMetres));
        RequirePositiveDepth(depthMetres);
        RequireFinitePositive(
            densityKilogramsPerCubicMetre,
            nameof(densityKilogramsPerCubicMetre));
        RequireFiniteNonNegative(
            surfaceTensionNewtonsPerMetre,
            nameof(surfaceTensionNewtonsPerMetre));
        RequireFinitePositive(
            gravityMetresPerSecondSquared,
            nameof(gravityMetresPerSecondSquared));
        double waveNumberPerMetre = 2.0 * Math.PI / wavelengthMetres;
        double depthResponse = ResolveDepthResponse(waveNumberPerMetre, depthMetres);
        double angularFrequencySquared = (
            gravityMetresPerSecondSquared * waveNumberPerMetre
            + surfaceTensionNewtonsPerMetre
                / densityKilogramsPerCubicMetre
                * waveNumberPerMetre * waveNumberPerMetre * waveNumberPerMetre)
            * depthResponse;
        return Math.Sqrt(angularFrequencySquared) / waveNumberPerMetre;
    }

    /// <summary>
    /// Computes the group velocity of a localized gravity-capillary wave packet
    /// at finite depth from
    /// omega^2=(g*k+(sigma/rho)*k^3)*tanh(k*h). Unlike phase velocity, this is
    /// the physical speed at which the energy envelope from an object impact
    /// travels across the surface.
    /// </summary>
    /// <param name="wavelengthMetres">Positive dominant wavelength in metres.</param>
    /// <param name="depthMetres">Positive water depth in metres, or positive infinity for deep water.</param>
    /// <param name="densityKilogramsPerCubicMetre">Positive liquid density in kg/m^3.</param>
    /// <param name="surfaceTensionNewtonsPerMetre">Non-negative surface tension in N/m.</param>
    /// <param name="gravityMetresPerSecondSquared">Positive gravitational acceleration in m/s^2.</param>
    /// <returns>Finite positive wave-packet group velocity in metres per second.</returns>
    public static double GravityCapillaryGroupVelocityMetresPerSecond(
        double wavelengthMetres,
        double depthMetres,
        double densityKilogramsPerCubicMetre,
        double surfaceTensionNewtonsPerMetre,
        double gravityMetresPerSecondSquared = StandardGravityMetresPerSecondSquared)
    {
        RequireFinitePositive(wavelengthMetres, nameof(wavelengthMetres));
        RequirePositiveDepth(depthMetres);
        RequireFinitePositive(
            densityKilogramsPerCubicMetre,
            nameof(densityKilogramsPerCubicMetre));
        RequireFiniteNonNegative(
            surfaceTensionNewtonsPerMetre,
            nameof(surfaceTensionNewtonsPerMetre));
        RequireFinitePositive(
            gravityMetresPerSecondSquared,
            nameof(gravityMetresPerSecondSquared));

        double waveNumberPerMetre = 2.0 * Math.PI / wavelengthMetres;
        double capillaryCoefficient = surfaceTensionNewtonsPerMetre
            / densityKilogramsPerCubicMetre;
        double restoring = gravityMetresPerSecondSquared * waveNumberPerMetre
            + capillaryCoefficient
                * waveNumberPerMetre * waveNumberPerMetre * waveNumberPerMetre;
        bool deepWater = IsDeepWater(waveNumberPerMetre, depthMetres);
        double depthResponse = ResolveDepthResponse(waveNumberPerMetre, depthMetres);
        double angularFrequency = Math.Sqrt(restoring * depthResponse);
        double dispersionDerivative = (
            gravityMetresPerSecondSquared
            + 3.0 * capillaryCoefficient
                * waveNumberPerMetre * waveNumberPerMetre) * depthResponse;
        if (!deepWater)
        {
            double hyperbolicSecantSquared = Math.Max(
                0.0,
                1.0 - depthResponse * depthResponse);
            dispersionDerivative += restoring
                * depthMetres
                * hyperbolicSecantSquared;
        }

        return dispersionDerivative / (2.0 * angularFrequency);
    }

    /// <summary>Validates a finite positive depth or the explicit deep-water sentinel.</summary>
    /// <param name="depthMetres">Depth in metres.</param>
    private static void RequirePositiveDepth(double depthMetres)
    {
        if ((!double.IsFinite(depthMetres) && !double.IsPositiveInfinity(depthMetres))
            || depthMetres <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(depthMetres));
        }
    }

    /// <summary>Returns whether finite-depth corrections are numerically negligible.</summary>
    /// <param name="waveNumberPerMetre">Positive angular wave number in rad/m.</param>
    /// <param name="depthMetres">Positive depth or infinity.</param>
    /// <returns>Whether tanh(kh) is indistinguishable from one at renderer precision.</returns>
    private static bool IsDeepWater(double waveNumberPerMetre, double depthMetres)
    {
        return double.IsPositiveInfinity(depthMetres)
            || waveNumberPerMetre * depthMetres >= 20.0;
    }

    /// <summary>Evaluates the finite-depth tanh(kh) response without overflowing hyperbolics.</summary>
    /// <param name="waveNumberPerMetre">Positive angular wave number in rad/m.</param>
    /// <param name="depthMetres">Positive depth or infinity.</param>
    /// <returns>Depth response in the open interval zero to one.</returns>
    private static double ResolveDepthResponse(double waveNumberPerMetre, double depthMetres)
    {
        return IsDeepWater(waveNumberPerMetre, depthMetres)
            ? 1.0
            : Math.Tanh(waveNumberPerMetre * depthMetres);
    }

    /// <summary>
    /// Computes the small-amplitude deep-water viscous decay rate 2*nu*k^2, where
    /// nu=dynamic viscosity/density and k=2*pi/wavelength.
    /// </summary>
    /// <param name="wavelengthMetres">Positive wavelength in metres.</param>
    /// <param name="densityKilogramsPerCubicMetre">Positive liquid density in kg/m^3.</param>
    /// <param name="dynamicViscosityPascalSeconds">Non-negative dynamic viscosity in Pa*s.</param>
    /// <returns>Amplitude damping rate in s^-1, suitable for exp(-rate*seconds).</returns>
    public static double ViscousAmplitudeDampingPerSecond(
        double wavelengthMetres,
        double densityKilogramsPerCubicMetre,
        double dynamicViscosityPascalSeconds)
    {
        RequireFinitePositive(wavelengthMetres, nameof(wavelengthMetres));
        RequireFinitePositive(
            densityKilogramsPerCubicMetre,
            nameof(densityKilogramsPerCubicMetre));
        RequireFiniteNonNegative(
            dynamicViscosityPascalSeconds,
            nameof(dynamicViscosityPascalSeconds));
        double waveNumberPerMetre = 2.0 * Math.PI / wavelengthMetres;
        double kinematicViscositySquareMetresPerSecond =
            dynamicViscosityPascalSeconds / densityKilogramsPerCubicMetre;
        return 2.0 * kinematicViscositySquareMetresPerSecond
            * waveNumberPerMetre * waveNumberPerMetre;
    }

    /// <summary>
    /// Returns the wind-driven portion of the Cox-Munk clean-water mean-square slope. The
    /// measured ocean relation is 0.003 + 0.00512 U; VintageRTX omits the 0.003 background
    /// swell floor because a lake or vessel does not intrinsically contain ocean swell.
    /// </summary>
    /// <param name="windSpeedMetresPerSecond">Non-negative wind speed in m/s.</param>
    /// <returns>Dimensionless wind-driven mean-square surface slope.</returns>
    public static double CleanWaterWindDrivenMeanSquareSlope(double windSpeedMetresPerSecond)
    {
        RequireFiniteNonNegative(windSpeedMetresPerSecond, nameof(windSpeedMetresPerSecond));
        return 0.00512 * windSpeedMetresPerSecond;
    }

    /// <summary>Evaluates Planck spectral radiance for a thermally emitting surface.</summary>
    /// <param name="wavelengthMetres">Positive wavelength in metres.</param>
    /// <param name="temperatureKelvins">Positive absolute temperature in kelvins.</param>
    /// <param name="emissivity">Spectral emissivity in the inclusive interval 0..1.</param>
    /// <returns>Spectral radiance in W*sr^-1*m^-3.</returns>
    public static double PlanckSpectralRadianceWattsPerSteradianCubicMetre(
        double wavelengthMetres,
        double temperatureKelvins,
        double emissivity)
    {
        RequireFinitePositive(wavelengthMetres, nameof(wavelengthMetres));
        RequireFinitePositive(temperatureKelvins, nameof(temperatureKelvins));
        if (!double.IsFinite(emissivity) || emissivity < 0.0 || emissivity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(emissivity));
        }

        double wavelengthSquared = wavelengthMetres * wavelengthMetres;
        double wavelengthFifth = wavelengthSquared * wavelengthSquared * wavelengthMetres;
        double exponent = PlanckConstantJouleSeconds * SpeedOfLightMetresPerSecond
            / (wavelengthMetres * BoltzmannConstantJoulesPerKelvin * temperatureKelvins);
        double denominator = wavelengthFifth * (Math.Exp(exponent) - 1.0);
        return emissivity
            * 2.0 * PlanckConstantJouleSeconds
            * SpeedOfLightMetresPerSecond * SpeedOfLightMetresPerSecond
            / denominator;
    }

    /// <summary>Rejects NaN, infinity, zero, and negative SI quantities.</summary>
    /// <param name="value">Candidate scalar.</param>
    /// <param name="parameterName">Public argument name used by the exception.</param>
    private static void RequireFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    /// <summary>Rejects NaN, infinity, and negative SI quantities.</summary>
    /// <param name="value">Candidate scalar.</param>
    /// <param name="parameterName">Public argument name used by the exception.</param>
    private static void RequireFiniteNonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
