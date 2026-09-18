"""Reconcile the parallel contract/roof-fixture changes with the full blocker repair.
Called once after apply_test_blockers.py; no test or production threshold is suppressed.
"""
from pathlib import Path
import runpy
ROOT = Path(__file__).resolve().parents[2]
runpy.run_path(str(ROOT/'tools/recovery/complete_test_blockers.py'), run_name='__main__')

def edit(path, old, new):
    p=ROOT/path
    s=p.read_text(encoding='utf-8')
    if s.count(old)!=1: raise ValueError(f'Expected exactly one anchor in {path}: '+old[:100])
    p.write_text(s.replace(old,new,1),encoding='utf-8',newline='\n')

# Retain the held-light contract migration introduced on the branch concurrently.
edit('tests/VintageRTX.Test/PreflightSuite.cs',
'''        Assert(shader.Contains("cameraAlignedLight", StringComparison.Ordinal)
            && shader.Contains("floatingWorldOrigin) < 0.75", StringComparison.Ordinal),
            "camera-aligned held-light self-intersection guard missing");''',
'''        Assert(!shader.Contains("floatingWorldOrigin) < 0.75", StringComparison.Ordinal)
            && shader.Contains("visibility += traceVoxelVisibility", StringComparison.Ordinal),
            "held emitters must trace world occlusion, not receive unconditional visibility");''')

# Keep the separate roof/ground fixture, but use Block.BlockId: Block.Id is read-only.
edit('tests/VintageRTX.Test/RuntimeCoverageProbeWorldTests.cs',
'''        harness.RainHeightAt = static (x, z) => Math.Abs(x) <= 1 && Math.Abs(z) <= 1 ? 82 : 78;
        harness.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid
                && (y == harness.RainHeightAt(x, z)
                    || (Math.Abs(x) <= 1 && Math.Abs(z) <= 1 && y == 80))
                ? harness.Solid
                : harness.Air;''',
'''        // A real ground receiver must not be mislabeled as another roofing block.
        Block ground = new()
        {
            BlockId = 2,
            Code = new AssetLocation("game:rock-granite"),
            BlockMaterial = EnumBlockMaterial.Stone,
            LightAbsorption = 32
        };
        harness.RainHeightAt = static (x, z) => Math.Abs(x) <= 1 && Math.Abs(z) <= 1 ? 82 : 78;
        harness.FallbackBlockAt = (x, y, z, layer) =>
            layer == BlockLayersAccess.Fluid ? harness.Air
                : Math.Abs(x) <= 1 && Math.Abs(z) <= 1 && (y == 82 || y == 80) ? harness.Solid
                : y == 78 ? ground : harness.Air;''')

# A missing calendar resolves to the documented world-up fallback. It cannot prove a
# real exterior shadow direction any more than the explicitly vertical-sun case can.
edit('tests/VintageRTX.Test/RuntimeCoverageProbeWorldTests.cs',
'''        Assert.IsTrue((bool)Invoke(fallbackSun, "TryApplyExteriorRoofCamera")!);
        fallbackSun.Dispose();''',
'''        Assert.IsFalse((bool)Invoke(fallbackSun, "TryApplyExteriorRoofCamera")!,
            "An absent calendar must not fabricate a real exterior shadow witness.");
        Assert.IsFalse(GetField<bool>(fallbackSun, "exteriorPositionLocked"));
        Assert.IsFalse(noClientCalendar.ChatMessages.Any(static text => text.StartsWith("/tp =", StringComparison.Ordinal)));
        Assert.IsTrue(noClientCalendar.Logs.Any(static entry => entry.Message.Contains(
            "vertical sun has no exposed horizontal shadow witness", StringComparison.Ordinal)));
        fallbackSun.Dispose();''')

p='tests/VintageRTX.Test/PreflightSuite.cs'
edit(p,
'''        Assert(
            renderer.Contains("reflectionDiagnosticCapture", StringComparison.Ordinal)
                && renderer.Contains("effectiveReflectionSteps = reflectionDiagnosticCapture", StringComparison.Ordinal)
                && renderer.Contains("effectiveVoxelReflectionSteps = voxelReflectionDiagnosticCapture", StringComparison.Ordinal),
            "performance-tier reflection diagnostics must temporarily restore their full trace length");''',
'''        Assert(
            !renderer.Contains("reflectionDiagnosticCapture", StringComparison.Ordinal)
                && !renderer.Contains("voxelReflectionDiagnosticCapture", StringComparison.Ordinal)
                && !renderer.Contains("voxelBounceDiagnosticCapture", StringComparison.Ordinal)
                && renderer.Contains("effectiveReflectionSteps = adaptiveQualityLevel switch", StringComparison.Ordinal)
                && renderer.Contains("effectiveVoxelReflectionSteps = adaptiveQualityLevel switch", StringComparison.Ordinal)
                && renderer.Contains("effectiveVoxelBounceRayCount = adaptiveQualityLevel switch", StringComparison.Ordinal),
            "native diagnostics must use exactly the runtime profile's reflection and bounce budgets");''')
edit(p,
'''                && renderer.Contains("VintageRtxRenderProfile.Extreme => 6", StringComparison.Ordinal)
                && renderer.Contains("VintageRtxRenderProfile.Cinematic => 8", StringComparison.Ordinal),''',
'''                && renderer.Contains("int maximumVoxelLights = VoxelScene.MaximumLightCount;", StringComparison.Ordinal),''')
edit(p,
'''                && renderer.Contains("range / (1.0f + viewDistanceSquared * 0.35f)", StringComparison.Ordinal)''',
'''                && renderer.Contains("EntityLightCollector", StringComparison.Ordinal)
                && renderer.Contains("entity.SourceIndex", StringComparison.Ordinal)''')
edit(p,
'''                    "result.indirect += sampleReflectionSource(hitUv) * confidence;",''',
'''                    "result.indirect += srgbToLinear(sampleReflectionSource(hitUv)) * confidence;",''')
edit(p,
'''                < renderer.IndexOf("bool voxelReflectionDiagnosticCapture", StringComparison.Ordinal),''',
'''                >= 0
                && renderer.IndexOf("UpdateAdaptiveQuality(config, captureFrame);", StringComparison.Ordinal)
                    < renderer.IndexOf("int effectiveReflectionSteps = adaptiveQualityLevel switch", StringComparison.Ordinal),''')
# Preserve all existing mathematical stable-selection assertions below the wiring checks.
# Profiles change quadrature quality, not the maximum represented number of local sources.
edit(p,
'''            "performance tier must preserve three sources with a stationary centre-balanced emitter sequence");''',
'''            "performance tier must retain stationary centre-balanced emitter sampling");
        Assert(renderer.Contains("int maximumVoxelLights = VoxelScene.MaximumLightCount;", StringComparison.Ordinal)
            && VoxelScene.MaximumLightCount == 8,
            "profile changes must not shrink the represented source set");''')
