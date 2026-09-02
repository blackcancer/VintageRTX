using System.Globalization;
using System.Text.RegularExpressions;

namespace VintageRTX.Test;

/// <summary>
/// Supports runtime Log Validator within the deterministic VintageRTX test infrastructure.
/// </summary>
internal static partial class RuntimeLogValidator
{
    /// <summary>
    /// Validates requested fixture operation and returns or emits actionable diagnostics for a failing test.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="scenario">The scenario input used to configure this deterministic test path.</param>
    /// <returns>A stable sequence or view used by the test runner and its assertions.</returns>
    public static IReadOnlyList<string> Validate(string log, ScenarioDefinition scenario)
    {
        List<string> failures = [];
        if (HasFatalFailure(log))
        {
            failures.Add("VintageRTX emitted an error during the runtime scenario");
        }

        foreach (string token in CommonRequiredTokens.Concat(scenario.RequiredLogTokens))
        {
            if (!log.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"missing log token: {token}");
            }
        }

        if (log.Contains("[VintageRTX] Display pass disabled", StringComparison.OrdinalIgnoreCase)
            || log.Contains("[VintageRTX] PBR terrain bridge disabled", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("VintageRTX disabled a rendering path");
        }

        if (PbrSidecarUsedAsAlbedoRegex().IsMatch(log))
        {
            failures.Add("a _n/_r/_m/_e PBR sidecar was requested as a block, item or entity albedo");
        }

        Dictionary<string, int> filteredAtlasVariants = AtlasWildcardFilterRegex()
            .Matches(log)
            .Cast<Match>()
            .Where(match => int.TryParse(match.Groups[2].Value, out _))
            .GroupBy(match => match.Groups[1].Value.ToLowerInvariant())
            .ToDictionary(
                group => group.Key,
                group => group.Sum(match =>
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
        string[] requiredFilteredAtlases = ["blocks", "items", "entities"];
        if (requiredFilteredAtlases.Any(target => !filteredAtlasVariants.ContainsKey(target))
            || filteredAtlasVariants.Values.Sum() <= 0)
        {
            failures.Add("PBR wildcard filtering did not cover all three vanilla albedo atlases");
        }

        Match geometry = GeometryRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (geometry.Success
            && int.TryParse(geometry.Groups[1].Value, out int fallback)
            && fallback > 128)
        {
            failures.Add($"nonstandard geometry fallback count too high: {fallback}");
        }

        Match indexedSidecars = IndexedSidecarRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (!indexedSidecars.Success
            || !int.TryParse(indexedSidecars.Groups[1].Value, out int indexedTotal)
            || !int.TryParse(indexedSidecars.Groups[2].Value, out int indexedNormals)
            || !int.TryParse(indexedSidecars.Groups[3].Value, out int indexedRoughness)
            || !int.TryParse(indexedSidecars.Groups[4].Value, out int indexedMetallic)
            || !int.TryParse(indexedSidecars.Groups[5].Value, out int indexedEmissive)
            || indexedTotal <= 0
            || indexedTotal != indexedNormals + indexedRoughness + indexedMetallic + indexedEmissive)
        {
            failures.Add("PBR sidecars were not indexed consistently without hiding mod assets");
        }

        Match pbr = PbrManifestRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        int manifestOverrides = pbr.Success
            && int.TryParse(pbr.Groups[1].Value, out int parsedManifestOverrides)
                ? parsedManifestOverrides
                : 0;
        if (manifestOverrides <= 0)
        {
            failures.Add("embedded base PBR assets did not apply any manifest texture override");
        }

        Match atlasLookup = PbrAtlasLookupRegex().Matches(log).Cast<Match>().LastOrDefault() ?? Match.Empty;
        if (atlasLookup.Success
            && int.TryParse(atlasLookup.Groups[1].Value, out int exactPlacements)
            && manifestOverrides > exactPlacements)
        {
            failures.Add(
                $"PBR uploads {manifestOverrides} exceed {exactPlacements} exact atlas placements; "
                + "a rectangle can still be overwritten by multiple source textures");
        }

        MatchCollection benchmarkMatches = BenchmarkRegex().Matches(log);
        int benchmarkMarkers = log.Split('\n').Count(static line =>
            line.Contains("Stabilized A/B/A result", StringComparison.OrdinalIgnoreCase));
        if (scenario.RunBenchmark && (benchmarkMarkers != 1 || benchmarkMatches.Count != 1))
        {
            failures.Add("stabilized A/B/A metrics are missing or malformed");
        }
        else if (scenario.RunBenchmark && benchmarkMatches.Count == 1)
        {
            Match benchmark = benchmarkMatches[0];
            double baselineFps = Parse(benchmark.Groups[1].Value);
            double baselineLow = Parse(benchmark.Groups[2].Value);
            double baselineJitter = Parse(benchmark.Groups[3].Value);
            double baselineGpu = Parse(benchmark.Groups[4].Value);
            double effectFps = Parse(benchmark.Groups[5].Value);
            double effectLow = Parse(benchmark.Groups[6].Value);
            double effectJitter = Parse(benchmark.Groups[7].Value);
            double gpu = Parse(benchmark.Groups[8].Value);
            double fpsCost = Parse(benchmark.Groups[9].Value);
            double lowCost = Parse(benchmark.Groups[10].Value);
            bool validSamples = baselineFps > 0.0
                && baselineLow > 0.0
                && baselineJitter >= 0.0
                && baselineGpu >= 0.0
                && effectFps > 0.0
                && effectLow > 0.0
                && effectJitter >= 0.0
                && gpu > 0.0;
            bool coherentDeltas = Math.Abs(fpsCost - (baselineFps - effectFps)) <= 0.2
                && Math.Abs(lowCost - (baselineLow - effectLow)) <= 0.2;
            if (!validSamples || !coherentDeltas)
            {
                failures.Add("stabilized A/B/A metrics contain invalid samples or inconsistent deltas");
            }

            double averageFrameTimeCost = FrameTimeCostMilliseconds(
                baselineFps,
                effectFps);
            double onePercentLowFrameTimeCost = FrameTimeCostMilliseconds(
                baselineLow,
                effectLow);
            double jitterIncrease = effectJitter - baselineJitter;
            if (validSamples && coherentDeltas && gpu > scenario.MaximumGpuMilliseconds)
            {
                failures.Add($"GPU cost {gpu:0.00}ms exceeds {scenario.MaximumGpuMilliseconds:0.00}ms");
            }
            if (validSamples
                && coherentDeltas
                && averageFrameTimeCost > scenario.MaximumAverageFrameTimeCostMilliseconds)
            {
                failures.Add(
                    $"average frame-time cost {averageFrameTimeCost:0.00}ms exceeds "
                    + $"{scenario.MaximumAverageFrameTimeCostMilliseconds:0.00}ms");
            }
            if (validSamples
                && coherentDeltas
                && onePercentLowFrameTimeCost
                    > scenario.MaximumOnePercentLowFrameTimeCostMilliseconds)
            {
                failures.Add(
                    $"1% low frame-time cost {onePercentLowFrameTimeCost:0.00}ms exceeds "
                    + $"{scenario.MaximumOnePercentLowFrameTimeCostMilliseconds:0.00}ms");
            }
            if (validSamples && coherentDeltas && effectFps < scenario.MinimumEffectFps)
            {
                failures.Add(
                    $"effect average {effectFps:0.0} FPS is below "
                    + $"{scenario.MinimumEffectFps:0.0} FPS");
            }
            if (validSamples
                && coherentDeltas
                && effectLow < scenario.MinimumEffectOnePercentLowFps)
            {
                failures.Add(
                    $"effect 1% low {effectLow:0.0} FPS is below "
                    + $"{scenario.MinimumEffectOnePercentLowFps:0.0} FPS");
            }
            if (validSamples
                && coherentDeltas
                && jitterIncrease > scenario.MaximumJitterIncreaseMilliseconds)
            {
                failures.Add(
                    $"effect jitter increase {jitterIncrease:0.00}ms exceeds "
                    + $"{scenario.MaximumJitterIncreaseMilliseconds:0.00}ms");
            }
        }

        if (string.Equals(scenario.Name, "nonstandard-geometry", StringComparison.OrdinalIgnoreCase))
        {
            int firstCaptureIndex = log.IndexOf("Comparison capture saved:", StringComparison.OrdinalIgnoreCase);
            int benchmarkIndex = log.IndexOf("Stabilized A/B/A result", StringComparison.OrdinalIgnoreCase);
            int readySearchEnd = firstCaptureIndex >= 0
                ? firstCaptureIndex
                : benchmarkIndex >= 0 ? benchmarkIndex : log.Length;
            int lastReadyIndex = readySearchEnd > 0
                ? log.LastIndexOf(
                    "Voxel scene generation ",
                    readySearchEnd - 1,
                    StringComparison.OrdinalIgnoreCase)
                : -1;
            int validationEnd = benchmarkIndex >= 0 ? benchmarkIndex : log.Length;
            if (lastReadyIndex >= 0 && validationEnd > lastReadyIndex)
            {
                string geometryWindow = log[lastReadyIndex..validationEnd];
                int tessellationFailures = FenceTessellationFailureRegex().Matches(geometryWindow).Count;
                if (tessellationFailures > 0)
                {
                    failures.Add(
                        $"nonstandard block tessellation failed while building the real-case geometry scene "
                        + $"(count={tessellationFailures})");
                }
            }
        }

        if (string.Equals(scenario.Name, "resize-and-reload", StringComparison.OrdinalIgnoreCase))
        {
            Match[] resized = ResourceResizeRegex().Matches(log)
                .Cast<Match>()
                .Where(match => match.Groups[1].Value != "0" && match.Groups[2].Value != "0")
                .ToArray();
            if (resized.Length < 2)
            {
                failures.Add("fewer than two non-initial GPU resource resize transitions were observed");
            }
            else
            {
                Match first = resized[0];
                Match last = resized[^1];
                if (first.Groups[1].Value != last.Groups[3].Value
                    || first.Groups[2].Value != last.Groups[4].Value)
                {
                    failures.Add("the final GPU resource size did not return to the original framebuffer size");
                }
            }
        }

        if (string.Equals(scenario.Name, "water-reflection", StringComparison.OrdinalIgnoreCase))
        {
            ValidateWaterReflectionWitnesses(log, failures);
            ValidateWaterReflectionImpacts(log, failures);
            ValidateWaterReflectionProjectiles(log, failures);
            ValidateWaterReflectionProjectileCaptures(log, failures);
        }

        return failures;
    }

    /// <summary>
    /// Proves that two server-authoritative, non-first-person entities remained
    /// above the liquid while the raw source and final reflection captures ran.
    /// </summary>
    /// <param name="log">Complete runtime log for one isolated scenario.</param>
    /// <param name="failures">Mutable validation diagnostics.</param>
    private static void ValidateWaterReflectionWitnesses(string log, List<string> failures)
    {
        Match[] requests = WaterReflectionWitnessRequestRegex().Matches(log).Cast<Match>().ToArray();
        Match[] spawns = WaterReflectionWitnessSpawnRegex().Matches(log).Cast<Match>().ToArray();
        Match[] stable = WaterReflectionWitnessStableRegex().Matches(log).Cast<Match>().ToArray();
        if (requests.Length != 1 || spawns.Length != 1)
        {
            failures.Add(
                "water reflection witness validation: expected exactly one parseable request and spawn, "
                + $"found requests={requests.Length}, spawns={spawns.Length}");
            return;
        }

        Match request = requests[0];
        Match spawn = spawns[0];
        const double maximumAnchorErrorMetres = 0.03;
        int[] spawnCoordinateGroups = [2, 3, 4, 6, 7, 8];
        for (int coordinate = 0; coordinate < spawnCoordinateGroups.Length; coordinate++)
        {
            double requested = Parse(request.Groups[coordinate + 1].Value);
            double observed = Parse(spawn.Groups[spawnCoordinateGroups[coordinate]].Value);
            if (Math.Abs(requested - observed) > maximumAnchorErrorMetres)
            {
                failures.Add(
                    "water reflection witness validation: spawned witness anchors do not match "
                    + "the deterministic client request");
                break;
            }
        }

        long opaqueEntityId = long.Parse(spawn.Groups[1].Value, CultureInfo.InvariantCulture);
        long alphaEntityId = long.Parse(spawn.Groups[5].Value, CultureInfo.InvariantCulture);
        if (opaqueEntityId <= 0 || alphaEntityId <= 0 || opaqueEntityId == alphaEntityId)
        {
            failures.Add("water reflection witness validation: entity identifiers are invalid or aliased");
        }

        Match? stableAfterWarmup = stable.FirstOrDefault(match =>
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == 1_000);
        Match? stableBeforeProjectiles = stable.FirstOrDefault(match =>
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == 1_500);
        Match? stableBeforeImpacts = stable.FirstOrDefault(match =>
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == 1_750);
        if (stableAfterWarmup is null
            || stableBeforeProjectiles is null
            || stableBeforeImpacts is null)
        {
            failures.Add(
                "water reflection witness validation: missing the 1000/1500/1750-tick stability checkpoints");
            return;
        }

        foreach (Match checkpoint in new[]
                 {
                     stableAfterWarmup,
                     stableBeforeProjectiles,
                     stableBeforeImpacts
                 })
        {
            long checkpointOpaqueId = long.Parse(checkpoint.Groups[2].Value, CultureInfo.InvariantCulture);
            long checkpointAlphaId = long.Parse(checkpoint.Groups[3].Value, CultureInfo.InvariantCulture);
            double maximumDrift = Parse(checkpoint.Groups[4].Value);
            if (checkpointOpaqueId != opaqueEntityId
                || checkpointAlphaId != alphaEntityId
                || maximumDrift > 0.25)
            {
                failures.Add(
                    "water reflection witness validation: a stability checkpoint changed identity "
                    + "or exceeded 0.25m drift");
            }
        }

        int rawSourceIndex = log.IndexOf(
            "impact-1-reflection-source-raw.png",
            StringComparison.OrdinalIgnoreCase);
        Match finalReflection = WaterReflectionDiagnosticCaptureRegex().Match(log);
        if (request.Index >= spawn.Index
            || spawn.Index >= stableAfterWarmup.Index
            || stableAfterWarmup.Index >= stableBeforeProjectiles.Index
            || stableBeforeProjectiles.Index >= stableBeforeImpacts.Index
            || stableBeforeImpacts.Index >= rawSourceIndex
            || rawSourceIndex < 0
            || !finalReflection.Success
            || rawSourceIndex >= finalReflection.Index)
        {
            failures.Add(
                "water reflection witness validation: evidence is not ordered as "
                + "request, spawn, 1000/1500/1750 stability, raw source, reflection capture");
        }
    }

    /// <summary>
    /// Correlates the three deterministic server requests with the client-side surface impulses.
    /// This deliberately rejects additional dropped-item impacts, including stale entities that
    /// were already present when the water camera was staged.
    /// </summary>
    /// <param name="log">Complete runtime log for one isolated scenario.</param>
    /// <param name="failures">Mutable validation diagnostics.</param>
    private static void ValidateWaterReflectionImpacts(string log, List<string> failures)
    {
        Match[] cameraTargets = WaterImpactCameraTargetRegex().Matches(log).Cast<Match>().ToArray();
        int cameraMarkers = log.Split('\n').Count(static line =>
            line.Contains("Water reflection camera applied", StringComparison.OrdinalIgnoreCase));
        if (cameraMarkers != 1 || cameraTargets.Length != 1)
        {
            failures.Add(
                "water impact validation: expected exactly one parseable camera target, "
                + $"found markers={cameraMarkers}, parsed={cameraTargets.Length}");
            return;
        }

        Match[] requests = WaterImpactRequestRegex().Matches(log).Cast<Match>().ToArray();
        Match[] spawns = WaterImpactSpawnRegex().Matches(log).Cast<Match>().ToArray();
        Match[] impacts = WaterImpactAppliedRegex().Matches(log).Cast<Match>().ToArray();
        int requestMarkers = log.Split('\n').Count(static line =>
            line.Contains("Server dropped-item impact requested", StringComparison.OrdinalIgnoreCase));
        int impactMarkers = log.Split('\n').Count(static line =>
            line.Contains("Dropped-item surface impact applied", StringComparison.OrdinalIgnoreCase));
        int spawnMarkers = log.Split('\n').Count(static line =>
            line.Contains("Server dropped-item impact spawned", StringComparison.OrdinalIgnoreCase));
        if (requestMarkers != 3
            || requests.Length != 3
            || spawnMarkers != 3
            || spawns.Length != 3
            || impactMarkers != 3
            || impacts.Length != 3)
        {
            failures.Add(
                "water impact validation: expected exactly three requested, spawned and applied impacts, "
                + $"found request markers={requestMarkers}, parsed requests={requests.Length}, "
                + $"spawn markers={spawnMarkers}, parsed spawns={spawns.Length}, "
                + $"applied markers={impactMarkers}, parsed impacts={impacts.Length}");
            return;
        }

        const double maximumCameraTargetDistanceMetres = 3.0;
        const double minimumVelocityScale = 0.18;
        const double maximumVelocityScale = 1.40;
        const double minimumDirectionCosine = 0.75;
        const double gravityMetresPerSecondSquared = 9.80665;
        // The scenario is positioned from terrestrial gravity so its authored target remains
        // deterministic, but Vintage Story advances entity Motion in nominal 1/60-second units
        // and applies the vanilla 0.37-per-second gravity constant.  The resulting maximum
        // ballistic acceleration is therefore 0.37 * 60 = 22.2 m/s^2.  Use that engine value
        // only for the coherence ceiling around the velocity measured at the real collision.
        const double maximumVanillaEntityGravityMetresPerSecondSquared = 0.37 * 60.0;
        const double maximumBallisticEnergyScale = 1.30;
        const double maximumInjectedDownwardSpeedMetresPerSecond = 4.25;
        const double impactVelocityRoundingHalfUnitMetresPerSecond = 0.0005;
        const double impactEnergyRoundingHalfUnitJoules = 0.00005;
        const double impactEnergyFloatSlackJoules = 0.00025;
        const double minimumObservedEnergyRatio = 1.35;
        const double maximumSurfaceHeightErrorMetres = 0.20;
        const double baseLandingTargetDistanceMetres = 0.25;
        const double maximumResidualTravelScale = 0.25;
        const double maximumSpawnCoordinateErrorMetres = 0.03;
        double targetX = Parse(cameraTargets[0].Groups[1].Value);
        double targetSurfaceY = Parse(cameraTargets[0].Groups[2].Value);
        double targetZ = Parse(cameraTargets[0].Groups[3].Value);
        double previousRequestedEnergy = double.NegativeInfinity;
        double previousRequestedHorizontalSpeed = double.NegativeInfinity;
        double previousRequestedDownwardSpeed = double.NegativeInfinity;
        double previousRequestedTargetX = double.NaN;
        double previousRequestedTargetZ = double.NaN;
        double previousObservedEnergy = double.NegativeInfinity;
        double previousObservedPeak = double.NegativeInfinity;
        HashSet<long> spawnedEntityIds = [];
        int[] expectedStackSizes = [1, 9, 64];
        double[] expectedDropHeightsMetres = [0.50, 3.00, 5.00];

        for (int index = 0; index < 3; index++)
        {
            Match request = requests[index];
            Match spawn = spawns[index];
            Match impact = impacts[index];
            int sequence = int.Parse(request.Groups[1].Value, CultureInfo.InvariantCulture);
            int impactCount = int.Parse(impact.Groups[1].Value, CultureInfo.InvariantCulture);
            int expectedSequence = index + 1;
            if (sequence != expectedSequence || impactCount != expectedSequence)
            {
                failures.Add(
                    $"water impact validation: expected request/count {expectedSequence}/3, "
                    + $"found sequence={sequence}/3 and count={impactCount}");
            }

            int nextRequestIndex = index + 1 < requests.Length
                ? requests[index + 1].Index
                : int.MaxValue;
            if (request.Index <= cameraTargets[0].Index
                || spawn.Index <= request.Index
                || impact.Index <= spawn.Index
                || impact.Index >= nextRequestIndex)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} was not logged between "
                    + "its request and the next deterministic request");
            }

            double dropHeightMetres = Parse(request.Groups[7].Value);
            int stackSize = int.Parse(request.Groups[8].Value, CultureInfo.InvariantCulture);
            double expectedMass = 0.35 * Math.Clamp(Math.Sqrt(stackSize), 1.0, 8.0);
            double loggedExpectedMass = Parse(request.Groups[9].Value);
            int spawnedStackSize = int.Parse(spawn.Groups[2].Value, CultureInfo.InvariantCulture);
            double spawnedExpectedMass = Parse(spawn.Groups[3].Value);
            double spawnedDropHeightMetres = Parse(spawn.Groups[4].Value);
            long spawnedEntityId = long.Parse(spawn.Groups[1].Value, CultureInfo.InvariantCulture);
            if (stackSize != expectedStackSizes[index]
                || spawnedStackSize != stackSize
                || Math.Abs(loggedExpectedMass - expectedMass) > 0.0005
                || Math.Abs(spawnedExpectedMass - expectedMass) > 0.0005
                || spawnedEntityId <= 0
                || !spawnedEntityIds.Add(spawnedEntityId))
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} stack, pseudo-mass or entity identity "
                    + "does not match the deterministic 1/9/64 sequence");
            }
            if (Math.Abs(dropHeightMetres - expectedDropHeightsMetres[index]) > 0.005
                || Math.Abs(spawnedDropHeightMetres - dropHeightMetres) > 0.005)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} drop height does not match "
                    + "the deterministic 0.50/3.00/5.00 m sequence");
            }

            double requestedVelocityX = Parse(request.Groups[10].Value);
            double requestedVelocityY = Parse(request.Groups[11].Value);
            double requestedVelocityZ = Parse(request.Groups[12].Value);
            double requestedHorizontalSpeed = Math.Sqrt(
                requestedVelocityX * requestedVelocityX
                + requestedVelocityZ * requestedVelocityZ);
            double requestedDownwardSpeed = -requestedVelocityY;
            if (requestedVelocityY >= 0.0
                || requestedDownwardSpeed > maximumInjectedDownwardSpeedMetresPerSecond)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} vertical launch velocity "
                    + "is outside the authored downward-release envelope");
            }
            if (index > 0
                && (requestedHorizontalSpeed < previousRequestedHorizontalSpeed + 0.20
                    || requestedDownwardSpeed < previousRequestedDownwardSpeed + 0.15))
            {
                failures.Add(
                    "water impact validation: requested launch velocities are not sufficiently separated");
            }

            double worldX = Parse(impact.Groups[2].Value);
            double worldZ = Parse(impact.Groups[3].Value);
            double targetDistance = Math.Sqrt(
                (worldX - targetX) * (worldX - targetX)
                + (worldZ - targetZ) * (worldZ - targetZ));
            if (targetDistance > maximumCameraTargetDistanceMetres)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} is {targetDistance:0.00}m "
                    + $"from camera target, above {maximumCameraTargetDistanceMetres:0.00}m");
            }

            double requestedTargetX = Parse(request.Groups[2].Value);
            double requestedTargetZ = Parse(request.Groups[3].Value);
            if (index > 0)
            {
                double requestedSeparation = Math.Sqrt(
                    (requestedTargetX - previousRequestedTargetX)
                        * (requestedTargetX - previousRequestedTargetX)
                    + (requestedTargetZ - previousRequestedTargetZ)
                        * (requestedTargetZ - previousRequestedTargetZ));
                if (requestedSeparation < 0.90)
                {
                    failures.Add(
                        "water impact validation: requested landing columns are not sufficiently separated");
                }
            }

            double ballisticFlightSeconds = (
                requestedVelocityY
                + Math.Sqrt(
                    requestedVelocityY * requestedVelocityY
                    + 2.0 * gravityMetresPerSecondSquared * dropHeightMetres))
                / gravityMetresPerSecondSquared;
            double requestedSpawnX = Parse(request.Groups[4].Value);
            double requestedSpawnY = Parse(request.Groups[5].Value);
            double requestedSpawnZ = Parse(request.Groups[6].Value);
            double expectedSpawnX = requestedTargetX - requestedVelocityX * ballisticFlightSeconds;
            double expectedSpawnY = targetSurfaceY + dropHeightMetres;
            double expectedSpawnZ = requestedTargetZ - requestedVelocityZ * ballisticFlightSeconds;
            if (Math.Abs(requestedSpawnX - expectedSpawnX) > maximumSpawnCoordinateErrorMetres
                || Math.Abs(requestedSpawnY - expectedSpawnY) > maximumSurfaceHeightErrorMetres
                || Math.Abs(requestedSpawnZ - expectedSpawnZ) > maximumSpawnCoordinateErrorMetres)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} requested spawn does not match "
                    + "its ballistic upstream point");
            }

            double requestedTargetDistance = Math.Sqrt(
                (worldX - requestedTargetX) * (worldX - requestedTargetX)
                + (worldZ - requestedTargetZ) * (worldZ - requestedTargetZ));
            double maximumLandingTargetDistanceMetres = baseLandingTargetDistanceMetres
                + maximumResidualTravelScale * requestedHorizontalSpeed * ballisticFlightSeconds;
            if (requestedTargetDistance > maximumLandingTargetDistanceMetres)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} is "
                    + $"{requestedTargetDistance:0.00}m from its requested target, above "
                    + $"{maximumLandingTargetDistanceMetres:0.00}m");
            }

            double spawnedX = Parse(spawn.Groups[5].Value);
            double spawnedY = Parse(spawn.Groups[6].Value);
            double spawnedZ = Parse(spawn.Groups[7].Value);
            if (Math.Abs(spawnedX - requestedSpawnX) > maximumSpawnCoordinateErrorMetres
                || Math.Abs(spawnedY - requestedSpawnY) > maximumSpawnCoordinateErrorMetres
                || Math.Abs(spawnedZ - requestedSpawnZ) > maximumSpawnCoordinateErrorMetres)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} server spawn does not match its request");
            }
            double spawnedSurfaceY = spawnedY - dropHeightMetres;
            if (Math.Abs(spawnedSurfaceY - targetSurfaceY) > maximumSurfaceHeightErrorMetres)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} spawn height implies surface "
                    + $"{spawnedSurfaceY:0.00}m instead of {targetSurfaceY:0.00}m");
            }

            double spawnedVelocityX = Parse(spawn.Groups[8].Value);
            double spawnedVelocityY = Parse(spawn.Groups[9].Value);
            double spawnedVelocityZ = Parse(spawn.Groups[10].Value);
            if (Math.Abs(spawnedVelocityX - requestedVelocityX) > 0.011
                || Math.Abs(spawnedVelocityY - requestedVelocityY) > 0.011
                || Math.Abs(spawnedVelocityZ - requestedVelocityZ) > 0.011)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} server velocity does not match its request");
            }

            double observedVelocityX = Parse(impact.Groups[6].Value);
            double observedVelocityY = Parse(impact.Groups[7].Value);
            double observedVelocityZ = Parse(impact.Groups[8].Value);
            double observedHorizontalSpeed = Math.Sqrt(
                observedVelocityX * observedVelocityX
                + observedVelocityZ * observedVelocityZ);
            if (observedVelocityY >= 0.0)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} vertical impact velocity is not descending");
            }
            double directionCosine = requestedHorizontalSpeed > 0.0
                && observedHorizontalSpeed > 0.0
                    ? (requestedVelocityX * observedVelocityX
                        + requestedVelocityZ * observedVelocityZ)
                        / (requestedHorizontalSpeed * observedHorizontalSpeed)
                    : double.NegativeInfinity;
            if (observedHorizontalSpeed < requestedHorizontalSpeed * minimumVelocityScale
                || observedHorizontalSpeed > requestedHorizontalSpeed * maximumVelocityScale
                || directionCosine < minimumDirectionCosine)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} horizontal velocity "
                    + $"({observedVelocityX:0.000},{observedVelocityZ:0.000}) m/s does not match "
                    + $"requested ({requestedVelocityX:0.000},{requestedVelocityZ:0.000}) m/s");
            }
            // Vintage Story applies contact drag independently on each item.
            // Preserve direction and a bounded per-impact speed ratio above,
            // but order impact strength by measured kinetic energy and surface
            // displacement below rather than by the horizontal component alone.

            double requestedEnergy = 0.5
                * expectedMass
                * (requestedVelocityX * requestedVelocityX
                    + requestedVelocityY * requestedVelocityY
                    + requestedVelocityZ * requestedVelocityZ
                    + 2.0 * maximumVanillaEntityGravityMetresPerSecondSquared * dropHeightMetres);
            double observedEnergy = Parse(impact.Groups[4].Value);
            double observedPeak = Parse(impact.Groups[5].Value);
            if (observedPeak <= 0.0)
            {
                failures.Add($"water impact validation: impact {expectedSequence} peak is not positive");
            }
            double velocityIdentityEnergy = 0.5
                * expectedMass
                * (observedVelocityX * observedVelocityX
                    + observedVelocityY * observedVelocityY
                    + observedVelocityZ * observedVelocityZ);
            double energyIdentityTolerance = expectedMass
                    * (impactVelocityRoundingHalfUnitMetresPerSecond
                            * (Math.Abs(observedVelocityX)
                                + Math.Abs(observedVelocityY)
                                + Math.Abs(observedVelocityZ))
                        + 1.5 * impactVelocityRoundingHalfUnitMetresPerSecond
                            * impactVelocityRoundingHalfUnitMetresPerSecond)
                + impactEnergyRoundingHalfUnitJoules
                + impactEnergyFloatSlackJoules;
            if (Math.Abs(observedEnergy - velocityIdentityEnergy) > energyIdentityTolerance)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} energy {observedEnergy:0.0000} J "
                    + $"does not match its logged impact velocity energy "
                    + $"{velocityIdentityEnergy:0.0000} J within {energyIdentityTolerance:0.0000} J");
            }

            double maximumCoherentEnergy = requestedEnergy * maximumBallisticEnergyScale;
            if (observedEnergy <= 0.0 || observedEnergy > maximumCoherentEnergy)
            {
                failures.Add(
                    $"water impact validation: impact {expectedSequence} energy {observedEnergy:0.0000} J "
                    + $"is non-positive or exceeds its ballistic upper bound "
                    + $"{maximumCoherentEnergy:0.0000} J");
            }

            if (index > 0 && requestedEnergy < previousRequestedEnergy * 1.75)
            {
                failures.Add(
                    "water impact validation: requested ballistic energies are not sufficiently separated");
            }
            if (index > 0
                && observedEnergy < previousObservedEnergy * minimumObservedEnergyRatio)
            {
                failures.Add(
                    "water impact validation: applied impact energies are not sufficiently increasing "
                    + $"(minimum ratio {minimumObservedEnergyRatio:0.00})");
            }
            if (index > 0 && observedPeak <= previousObservedPeak)
            {
                failures.Add("water impact validation: applied impact peaks are not strictly increasing");
            }
            previousRequestedEnergy = requestedEnergy;
            previousRequestedHorizontalSpeed = requestedHorizontalSpeed;
            previousRequestedDownwardSpeed = requestedDownwardSpeed;
            previousRequestedTargetX = requestedTargetX;
            previousRequestedTargetZ = requestedTargetZ;
            previousObservedEnergy = observedEnergy;
            previousObservedPeak = observedPeak;
        }
    }

    /// <summary>
    /// Proves that the stabilized water sequence spawned the concrete vanilla thrown-item and
    /// arrow entities, preserved their public projectile payloads, and observed the expected
    /// ricochet versus one-shot liquid-entry callbacks.
    /// </summary>
    /// <param name="log">Complete runtime log for one isolated scenario.</param>
    /// <param name="failures">Mutable validation diagnostics.</param>
    private static void ValidateWaterReflectionProjectiles(string log, List<string> failures)
    {
        Match[] requests = WaterProjectileRequestRegex().Matches(log).Cast<Match>().ToArray();
        Match[] spawns = WaterProjectileSpawnRegex().Matches(log).Cast<Match>().ToArray();
        Match[] applied = WaterProjectileAppliedRegex().Matches(log).Cast<Match>().ToArray();
        int requestMarkers = log.Split('\n').Count(static line =>
            line.Contains("Server liquid projectile requested", StringComparison.OrdinalIgnoreCase));
        int spawnMarkers = log.Split('\n').Count(static line =>
            line.Contains("Server liquid projectile spawned", StringComparison.OrdinalIgnoreCase));
        int appliedMarkers = log.Split('\n').Count(static line =>
            line.Contains("Projectile surface impact applied", StringComparison.OrdinalIgnoreCase));
        if (requestMarkers != 2
            || requests.Length != 2
            || spawnMarkers != 2
            || spawns.Length != 2
            || appliedMarkers < 2
            || applied.Length != appliedMarkers)
        {
            failures.Add(
                "water projectile validation: expected two requested/spawned projectiles and at least "
                + "two parseable callbacks, "
                + $"found requests={requestMarkers}/{requests.Length}, spawns={spawnMarkers}/{spawns.Length}, "
                + $"callbacks={appliedMarkers}/{applied.Length}");
            return;
        }

        Match? stable = WaterReflectionWitnessStableRegex().Matches(log)
            .Cast<Match>()
            .FirstOrDefault(static match =>
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == 1_750);
        if (stable is null || requests.Any(request => request.Index <= stable.Index))
        {
            failures.Add(
                "water projectile validation: both projectile requests must follow the 1750-tick stability checkpoint");
        }

        string[] expectedKinds = ["stone", "arrow"];
        string[] expectedRuntimeClasses = ["EntityThrownItem", "EntityProjectile"];
        string[] expectedEntityTypes = ["game:thrownitem", "game:arrow-flint"];
        string[] expectedPayloads = ["game:stone-granite", "game:arrow-flint"];
        double[] expectedMasses = [0.350, 0.060];
        HashSet<long> entityIds = [];
        for (int index = 0; index < expectedKinds.Length; index++)
        {
            Match request = requests[index];
            Match spawn = spawns[index];
            int sequence = int.Parse(request.Groups[1].Value, CultureInfo.InvariantCulture);
            string requestedKind = request.Groups[2].Value;
            string spawnedKind = spawn.Groups[1].Value;
            long entityId = long.Parse(spawn.Groups[2].Value, CultureInfo.InvariantCulture);
            if (sequence != index + 1
                || !string.Equals(requestedKind, expectedKinds[index], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(spawnedKind, expectedKinds[index], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(request.Groups[3].Value, expectedEntityTypes[index], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(request.Groups[4].Value, expectedPayloads[index], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(spawn.Groups[3].Value, expectedRuntimeClasses[index], StringComparison.Ordinal)
                || !string.Equals(spawn.Groups[4].Value, expectedEntityTypes[index], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(spawn.Groups[5].Value, expectedPayloads[index], StringComparison.OrdinalIgnoreCase)
                || entityId <= 0
                || !entityIds.Add(entityId))
            {
                failures.Add(
                    $"water projectile validation: sequence {index + 1} does not identify the required "
                    + $"{expectedRuntimeClasses[index]} entity and {expectedPayloads[index]} payload");
            }

            if (request.Index >= spawn.Index)
            {
                failures.Add(
                    $"water projectile validation: sequence {index + 1} spawned before its client request");
            }

            for (int coordinate = 0; coordinate < 3; coordinate++)
            {
                double requestedPosition = Parse(request.Groups[7 + coordinate].Value);
                double spawnedPosition = Parse(spawn.Groups[6 + coordinate].Value);
                double requestedVelocity = Parse(request.Groups[11 + coordinate].Value);
                double spawnedVelocity = Parse(spawn.Groups[9 + coordinate].Value);
                if (Math.Abs(requestedPosition - spawnedPosition) > 0.03
                    || Math.Abs(requestedVelocity - spawnedVelocity) > 0.011)
                {
                    failures.Add(
                        $"water projectile validation: sequence {index + 1} server position or velocity "
                        + "does not match its bounded client request");
                    break;
        }
    }

            Match[] entityCallbacks = applied
                .Where(match => long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) == entityId)
                .ToArray();
            if (entityCallbacks.Length == 0)
            {
                failures.Add(
                    $"water projectile validation: {expectedKinds[index]} entity {entityId} produced no liquid callback");
                continue;
            }

            if (index == 1 && entityCallbacks.Length != 1)
            {
                failures.Add("water projectile validation: the flint arrow must produce exactly one entry impulse");
            }

            foreach (Match callback in entityCallbacks)
            {
                string expectedSurfaceClass = index == 0 ? "ThrownStone" : "Projectile";
                string expectedImpulseKind = index == 0 ? "StoneRicochet" : "GenericEntry";
                double mass = Parse(callback.Groups[9].Value);
                double energy = Parse(callback.Groups[10].Value);
                double incidentY = Parse(callback.Groups[12].Value);
                double outgoingY = Parse(callback.Groups[15].Value);
                string motionSource = callback.Groups[17].Value;
                if (!string.Equals(callback.Groups[3].Value, expectedSurfaceClass, StringComparison.Ordinal)
                    || !string.Equals(callback.Groups[4].Value, expectedImpulseKind, StringComparison.Ordinal)
                    || Math.Abs(mass - expectedMasses[index]) > 0.0005
                    || energy <= 0.0
                    || incidentY >= 0.0
                    || (index == 0 && outgoingY <= 0.0)
                    || !string.Equals(
                        motionSource,
                        "server-authoritative",
                        StringComparison.Ordinal))
                {
                    failures.Add(
                        $"water projectile validation: {expectedKinds[index]} callback does not preserve "
                        + "its physical class, mass, authoritative motion, descending incident motion "
                        + "and expected response");
                }
            }
        }

        // This is deliberately a representative full-world gate: ambient creatures or other
        // gameplay systems may launch additional projectiles. Validate the two requested entity IDs
        // exactly, but do not turn legitimate unrelated collisions into a test contamination error.
    }

    /// <summary>
    /// Requires a saved final reference plus three ordered pre-impact surface fields and one saved
    /// surface-field/final pair after each real projectile callback. Matching complete save lines
    /// prevents queued labels from satisfying evidence, while the ordering rejects contamination by
    /// the later high-energy dropped items.
    /// </summary>
    /// <param name="log">Complete runtime log for one isolated scenario.</param>
    /// <param name="failures">Mutable validation diagnostics.</param>
    private static void ValidateWaterReflectionProjectileCaptures(string log, List<string> failures)
    {
        string[] labels =
        [
            "projectile-stone-baseline-final",
            "projectile-stone-baseline-earlier-surface-field",
            "projectile-stone-baseline-prior-surface-field",
            "projectile-stone-baseline-surface-field",
            "projectile-stone-surface-field",
            "projectile-stone-final",
            "projectile-arrow-baseline-final",
            "projectile-arrow-baseline-earlier-surface-field",
            "projectile-arrow-baseline-prior-surface-field",
            "projectile-arrow-baseline-surface-field",
            "projectile-arrow-surface-field",
            "projectile-arrow-final"
        ];
        int[] captureIndices = new int[labels.Length];
        for (int index = 0; index < labels.Length; index++)
        {
            string label = labels[index];
            string[] matchingLines = log.Split('\n')
                .Where(line => IsSavedCapturePairLine(line, label))
                .ToArray();
            if (matchingLines.Length != 1)
            {
                failures.Add(
                    $"water projectile capture validation: expected exactly one saved {label} pair, "
                    + $"found {matchingLines.Length}");
                captureIndices[index] = -1;
                continue;
            }

            captureIndices[index] = log.IndexOf(matchingLines[0], StringComparison.Ordinal);
        }

        if (captureIndices.Any(static index => index < 0))
        {
            return;
        }

        Match? stoneCallback = WaterProjectileAppliedRegex().Matches(log)
            .Cast<Match>()
            .FirstOrDefault(static match =>
                string.Equals(match.Groups[3].Value, "ThrownStone", StringComparison.Ordinal)
                && string.Equals(match.Groups[4].Value, "StoneRicochet", StringComparison.Ordinal));
        Match? arrowCallback = WaterProjectileAppliedRegex().Matches(log)
            .Cast<Match>()
            .FirstOrDefault(static match =>
                string.Equals(match.Groups[3].Value, "Projectile", StringComparison.Ordinal)
                && string.Equals(match.Groups[4].Value, "GenericEntry", StringComparison.Ordinal));
        Match? firstDroppedItemRequest = WaterImpactRequestRegex().Matches(log)
            .Cast<Match>()
            .FirstOrDefault();
        if (stoneCallback is null
            || arrowCallback is null
            || captureIndices[0] >= captureIndices[1]
            || captureIndices[1] >= captureIndices[2]
            || captureIndices[2] >= captureIndices[3]
            || captureIndices[3] >= captureIndices[4]
            || captureIndices[3] >= stoneCallback.Index
            || stoneCallback.Index >= captureIndices[4]
            || captureIndices[4] >= captureIndices[5]
            || captureIndices[5] >= captureIndices[6]
            || captureIndices[6] >= captureIndices[7]
            || captureIndices[7] >= captureIndices[8]
            || captureIndices[8] >= captureIndices[9]
            || captureIndices[9] >= arrowCallback.Index
            || arrowCallback.Index >= captureIndices[10]
            || captureIndices[10] >= captureIndices[11]
            || firstDroppedItemRequest is null
            || captureIndices[11] >= firstDroppedItemRequest.Index)
        {
            failures.Add(
                "water projectile capture validation: clean baseline/callback/surface-field/final "
                + "ordering was not preserved before the dropped-item sequence");
        }
    }

    /// <summary>Recognizes a complete saved before/effect pair for one stable capture label.</summary>
    /// <param name="line">Single runtime log line.</param>
    /// <param name="label">Stable event label without before/effect suffix.</param>
    /// <returns>Whether the line proves that both PNG files reached disk.</returns>
    private static bool IsSavedCapturePairLine(string line, string label) =>
        line.Contains("Comparison capture saved:", StringComparison.OrdinalIgnoreCase)
        && line.Contains($"{label}-before.png", StringComparison.OrdinalIgnoreCase)
        && line.Contains($"{label}-vintagertx.png", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether is Complete holds for the current synthetic fixture state.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <param name="scenario">The scenario input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    public static bool IsComplete(string log, ScenarioDefinition scenario)
    {
        if (!scenario.RequiredLogTokens.All(token =>
                log.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.Equals(
                scenario.Name,
                "water-reflection",
                StringComparison.OrdinalIgnoreCase))
        {
            int appliedImpactMarkers = log.Split('\n').Count(static line =>
                line.Contains(
                    "Dropped-item surface impact applied:",
                    StringComparison.OrdinalIgnoreCase));
            int projectileRequestMarkers = log.Split('\n').Count(static line =>
                line.Contains(
                    "Server liquid projectile requested:",
                    StringComparison.OrdinalIgnoreCase));
            int projectileSpawnMarkers = log.Split('\n').Count(static line =>
                line.Contains(
                    "Server liquid projectile spawned:",
                    StringComparison.OrdinalIgnoreCase));
            if (appliedImpactMarkers < 3
                || projectileRequestMarkers < 2
                || projectileSpawnMarkers < 2
                || !log.Contains(
                    "Server reflection witnesses stable: ticks=1750",
                    StringComparison.OrdinalIgnoreCase)
                || !log.Contains(
                    "class=ThrownStone, kind=StoneRicochet",
                    StringComparison.OrdinalIgnoreCase)
                || !log.Contains(
                    "class=Projectile, kind=GenericEntry",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (string label in new[]
            {
                "projectile-stone-baseline-final",
                "projectile-stone-baseline-earlier-surface-field",
                "projectile-stone-baseline-prior-surface-field",
                "projectile-stone-baseline-surface-field",
                "projectile-stone-final",
                "projectile-stone-surface-field",
                "projectile-arrow-baseline-final",
                "projectile-arrow-baseline-earlier-surface-field",
                "projectile-arrow-baseline-prior-surface-field",
                "projectile-arrow-baseline-surface-field",
                "projectile-arrow-final",
                "projectile-arrow-surface-field"
            })
            {
                if (!log.Split('\n').Any(line => IsSavedCapturePairLine(line, label)))
                {
                    return false;
                }
            }
        }

        string finalDiagnosticToken = scenario.CaptureProfile switch
        {
            "render-lab" => "-voxel-shadow-vintagertx.png",
            "water-reflection" => "-entity-mirror-vintagertx.png",
            _ => "-wetness-vintagertx.png"
        };
        if (!scenario.RunBenchmark)
        {
            // The focused render lab deliberately omits the 45-second A/B/A
            // benchmark. Its last requested diagnostic proves that the whole
            // short final/normal/material/shadow sequence reached disk.
            return log.Contains(finalDiagnosticToken, StringComparison.OrdinalIgnoreCase);
        }

        return log.Contains("Stabilized A/B/A result", StringComparison.OrdinalIgnoreCase)
            // The benchmark can complete before the delayed diagnostic capture
            // sequence reaches its first frame, especially at a capped 60 Hz.
            // Keep the game alive through the final channel so validation never
            // kills a healthy run before its PNG evidence has been written.
            && log.Contains(finalDiagnosticToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether has Fatal Failure holds for the current synthetic fixture state.
    /// </summary>
    /// <param name="log">The log input used to configure this deterministic test path.</param>
    /// <returns>True when the tested predicate holds for the current fixture state; otherwise false.</returns>
    public static bool HasFatalFailure(string log)
    {
        return log.Contains("[Error] [VintageRTX", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Executes the parse step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The parse result consumed by the caller&apos;s assertion.</returns>
    private static double Parse(string value)
    {
        return double.Parse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts paired FPS samples to added frame time, the quantity that remains comparable across refresh rates.
    /// </summary>
    /// <param name="baselineFps">Positive baseline frame rate.</param>
    /// <param name="effectFps">Positive effect-enabled frame rate.</param>
    /// <returns>Effect frame time minus baseline frame time in milliseconds.</returns>
    internal static double FrameTimeCostMilliseconds(double baselineFps, double effectFps)
    {
        if (!double.IsFinite(baselineFps)
            || !double.IsFinite(effectFps)
            || baselineFps <= 0.0
            || effectFps <= 0.0)
        {
            return double.PositiveInfinity;
        }

        return (1000.0 / effectFps) - (1000.0 / baselineFps);
    }

    private static readonly string[] CommonRequiredTokens =
    [
        "Clean renderer bootstrap complete",
        "PBR asset-discovery suffix filter installed",
        "PBR atlas wildcard filter applied",
        "PBR sidecars indexed without hiding mod assets",
        "File-backed PBR atlas loaded",
        "runtime generation=disabled",
        "Display pass ready",
        "Reflection G-buffer material, normal, and position attachments are ready",
        "Deterministic environment verified: PASS"
    ];

    /// <summary>
    /// Executes the geometry Regex step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <returns>The geometry Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"fallback=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GeometryRegex();

    /// <summary>
    /// Determines whether indexed Sidecar Regex holds for the current runtime log.
    /// </summary>
    /// <returns>The indexed sidecar cardinality expression used by runtime validation.</returns>
    [GeneratedRegex(@"PBR sidecars indexed without hiding mod assets: total=(\d+), normals=(\d+), roughness=(\d+), metallic=(\d+), emissive=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IndexedSidecarRegex();

    /// <summary>
    /// Executes the pbr Sidecar Used As Albedo Regex step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <returns>The pbr Sidecar Used As Albedo Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Texture asset '[^']+_(?:n|r|m|e)\.png' not found \(defined in (?:Baked variant of )?(?:block|item|entity|shape)", RegexOptions.IgnoreCase)]
    private static partial Regex PbrSidecarUsedAsAlbedoRegex();

    /// <summary>
    /// Executes the atlas Wildcard Filter Regex step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <returns>The atlas Wildcard Filter Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"PBR atlas wildcard filter applied: target=(blocks|items|entities), removed variants=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AtlasWildcardFilterRegex();

    /// <summary>
    /// Executes the pbr Manifest Regex step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <returns>The pbr Manifest Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"manifest overrides=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PbrManifestRegex();

    /// <summary>
    /// Executes the pbr Atlas Lookup Regex step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <returns>The pbr Atlas Lookup Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"exact placements=(\d+), ambiguous rectangles=(\d+), skipped ambiguous links=(\d+), skipped composite rectangles=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PbrAtlasLookupRegex();

    /// <summary>
    /// Executes the benchmark Regex step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <returns>The benchmark Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"^[^\r\n]*Stabilized A/B/A result \| baseline: fps=([0-9]+(?:[.,][0-9]+)?), 1%low=([0-9]+(?:[.,][0-9]+)?), jitter=([0-9]+(?:[.,][0-9]+)?)ms, gpu=([0-9]+(?:[.,][0-9]+)?)ms \| effect: fps=([0-9]+(?:[.,][0-9]+)?), 1%low=([0-9]+(?:[.,][0-9]+)?), jitter=([0-9]+(?:[.,][0-9]+)?)ms, gpu=([0-9]+(?:[.,][0-9]+)?)ms \| delta fps=([+\-]?[0-9]+(?:[.,][0-9]+)?), delta 1%low=([+\-]?[0-9]+(?:[.,][0-9]+)?)\.[\r]?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BenchmarkRegex();

    /// <summary>
    /// Matches one correlated engine exception and chunk-tessellator stack for
    /// a fence-stack-aware block, without joining unrelated log fragments.
    /// </summary>
    /// <returns>The fence tessellation failure matcher used by the real-case geometry gate.</returns>
    [GeneratedRegex(@"^[^\r\n]*\[Error\] Exception: Index was outside the bounds of the array\.\r?\n\s+at Vintagestory\.GameContent\.BlockFenceStackAware\.OnJsonTesselation[^\r\n]*\r?\n(?:\s+at [^\r\n]*\r?\n){0,6}?\s+at Vintagestory\.Client\.NoObf\.ChunkTesselator\.(?:TesselateBlock|BuildBlockPolygons|NowProcessChunk)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FenceTessellationFailureRegex();

    /// <summary>
    /// Executes the resource Resize Regex step used by the deterministic runtime Log Validator fixture.
    /// </summary>
    /// <returns>The resource Resize Regex result consumed by the caller&apos;s assertion.</returns>
    [GeneratedRegex(@"Size-dependent GPU resources resized: (\d+)x(\d+) -> (\d+)x(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceResizeRegex();

    /// <summary>Matches the horizontal impact target embedded in the staged water-camera log.</summary>
    /// <returns>The single camera-target matcher used to bound client impact positions.</returns>
    [GeneratedRegex(@"^[^\r\n]*Water reflection camera applied:[^\r\n]*\bimpact=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterImpactCameraTargetRegex();

    /// <summary>Matches one deterministic client request, landing target, upstream spawn, drop height, stack pseudo-mass and SI velocity.</summary>
    /// <returns>The ordered request matcher used to identify the injected entities.</returns>
    [GeneratedRegex(@"^[^\r\n]*Server dropped-item impact requested:\s*sequence=(\d+)/3,\s*target=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*spawn=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*drop-height=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*m,\s*stack=(\d+),\s*expected pseudo-mass=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*kg,\s*velocity=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\.[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterImpactRequestRegex();

    /// <summary>Matches the real server entity, stack pseudo-mass, drop height, position and SI velocity.</summary>
    /// <returns>The authoritative spawn matcher used to reject direct simulation injection.</returns>
    [GeneratedRegex(@"^[^\r\n]*Server dropped-item impact spawned:\s*entity=(\d+),\s*item=game:(?:stone|rock)-granite,\s*stack=(\d+),\s*expected pseudo-mass=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*kg,\s*drop-height=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*m,\s*position=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*velocity-si=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\s*m/s,[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterImpactSpawnRegex();

    /// <summary>Matches one applied dropped-item impulse, its full intersection velocity and client-observed diagnostics.</summary>
    /// <returns>The client impact matcher used for position, energy, and velocity correlation.</returns>
    [GeneratedRegex(@"^[^\r\n]*Dropped-item surface impact applied:\s*count=(\d+),\s*world=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),[^\r\n]*\benergy=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s+J,\s*peak=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*m,[^\r\n]*\bimpactVelocity=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\s*m/s\.[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterImpactAppliedRegex();

    /// <summary>Matches one post-stabilization request for a real stone or arrow projectile.</summary>
    /// <returns>The client request matcher used to correlate the bounded ballistic spawn.</returns>
    [GeneratedRegex(@"^[^\r\n]*Server liquid projectile requested:\s*sequence=(\d+)/2,\s*kind=(stone|arrow),\s*entity-type=([a-z0-9:_-]+),\s*payload=([a-z0-9:_-]+),\s*target=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*spawn=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*drop-height=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*m,\s*velocity=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\s*m/s\.[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterProjectileRequestRegex();

    /// <summary>Matches the concrete vanilla projectile class, entity type, payload and motion.</summary>
    /// <returns>The authoritative spawn matcher used to prove public <c>IProjectile</c> setup.</returns>
    [GeneratedRegex(@"^[^\r\n]*Server liquid projectile spawned:\s*kind=(stone|arrow),\s*entity=(\d+),\s*runtime-class=([A-Za-z0-9_]+),\s*entity-type=([a-z0-9:_-]+),\s*payload=([a-z0-9:_-]+),\s*position=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*velocity-si=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\s*m/s,[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterProjectileSpawnRegex();

    /// <summary>Matches one exact projectile/liquid callback consumed by the surface solver.</summary>
    /// <returns>The physical response matcher for stone ricochets and generic projectile entries.</returns>
    [GeneratedRegex(@"^[^\r\n]*Projectile surface impact applied:\s*sequence=(\d+),\s*entity=(\d+),\s*class=([A-Za-z0-9_]+),\s*kind=([A-Za-z0-9_]+),\s*world=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*cell=\((\d+),(\d+)\)/\d+x\d+,\s*mass=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*kg,\s*energy=([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*J,\s*incident=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\s*m/s,\s*outgoing=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\s*m/s\.\s*source=(server-authoritative|client-remote-motion)\.[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterProjectileAppliedRegex();

    /// <summary>Matches the two deterministic above-surface client witness anchors.</summary>
    /// <returns>The request matcher used to correlate both server-side entities.</returns>
    [GeneratedRegex(@"^[^\r\n]*Reflection witnesses requested:\s*opaque-item=game:stone-granite\s+at \(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*surface-clearance=1[.,]20m;\s*alpha-shaped=game:strawdummy\s+at \(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\),\s*surface-clearance=0[.,]04m\.[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterReflectionWitnessRequestRegex();

    /// <summary>Matches both authoritative witness identities, types and fixed anchors.</summary>
    /// <returns>The server spawn matcher used to prove distinct generic entities.</returns>
    [GeneratedRegex(@"^[^\r\n]*Server reflection witnesses spawned:\s*opaque entity=(\d+),\s*item=game:(?:stone|rock)-granite,\s*anchor=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\);\s*alpha-shaped entity=(\d+),\s*type=game:strawdummy,\s*anchor=\(\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*,\s*([+\-]?[0-9]+(?:[.,][0-9]+)?)\s*\)\.[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterReflectionWitnessSpawnRegex();

    /// <summary>Matches a fixed-anchor identity and maximum-drift checkpoint.</summary>
    /// <returns>The stability matcher used to bracket source and reflected captures.</returns>
    [GeneratedRegex(@"^[^\r\n]*Server reflection witnesses stable:\s*ticks=(\d+),\s*opaque entity=(\d+),\s*alpha-shaped entity=(\d+),\s*maximum drift=([+\-]?[0-9]+(?:[.,][0-9]+)?)m\.[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterReflectionWitnessStableRegex();

    /// <summary>Matches the final reflection diagnostic without accepting voxel-reflection output.</summary>
    /// <returns>The capture matcher that proves the generic-reflection channel reached disk.</returns>
    [GeneratedRegex(@"^[^\r\n]*(?<!voxel-)reflection-before\.png[^\r\n]*(?<!voxel-)reflection-vintagertx\.png[^\r\n]*\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex WaterReflectionDiagnosticCaptureRegex();
}
