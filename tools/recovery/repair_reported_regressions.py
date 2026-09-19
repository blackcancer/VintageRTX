"""One-shot migration from the reproduced 9acea0a9 regression baseline.
The publication workflow must execute all reported classes and production GLSL before committing
ordinary source files. Never run this recipe against an already migrated checkout.
"""
from pathlib import Path
import hashlib

ROOT = Path(__file__).resolve().parents[2]
changes = {}

def load(path, expected):
    data = (ROOT / path).read_bytes()
    digest = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
    if digest != expected:
        raise RuntimeError(f'{path}: unexpected base {digest}, expected {expected}')
    return data.decode('utf-8').replace('\r\n', '\n')

def once(source, old, new):
    if source.count(old) != 1:
        raise RuntimeError(f'Expected one migration anchor: {old[:180]!r}, got {source.count(old)}')
    return source.replace(old, new, 1)

def body(source, signature):
    start = source.index(signature)
    opening = source.index('{', start)
    depth, end = 1, opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return start, end, source[start:end]

p = 'src/VintageRTX/Rendering/EntityLightCollector.cs'
s = load(p, 'fa204b55e19ba87d659e7eb0c2be95b4028890f0')
s = once(s, '''        Entity? player = api.World.Player?.Entity;
        if (player is null) return lights;''', '''        // A client world and its entity collection can be absent during startup,
        // teardown, or a headless render fixture. Engine light arrays remain a
        // separate fallback; do not dereference a half-initialized world here.
        IClientWorldAccessor? world = api.World;
        Entity? player = world?.Player?.Entity;
        var loadedEntities = world?.LoadedEntities;
        if (player?.Pos is null || loadedEntities is null)
        {
            identities.Clear();
            return lights;
        }''')
s = once(s, 'foreach (Entity entity in api.World.LoadedEntities.Values)',
    'foreach (Entity entity in loadedEntities.Values)')
s = once(s, '''                if (!EmitterAppearance.TryResolve(source?.Collectible?.Code ?? entity.Code,''',
'''                AssetLocation? sourceCode = source?.Collectible?.Code ?? entity.Code;
                if (sourceCode is null || !EmitterAppearance.TryResolve(sourceCode,''')
s = once(s, '(source?.Collectible?.Code ?? entity.Code).ToString()', 'sourceCode.ToString()')
changes[p] = s

p = 'tests/VintageRTX.Test/CoverageFilmicResidualTests.cs'
s = load(p, '639b4d2ca33e3bf4457fd47d53d1932057de54b1')
a, b, method = body(s, 'public void CleanGBufferAndEntityMirrorReachValidatedNonGlBoundaries()')
start = method.index('            TargetInvocationException failure = Assert.ThrowsException<TargetInvocationException>(() =>')
end = method.index('            Assert.AreEqual(1, projectionStack.Count', start)
method = method[:start] + '''            // This fixture has no usable mirror depth projection. The public
            // rendering boundary now rejects it before touching OpenGL instead
            // of leaking a reflection-wrapped native-context exception.
            Assert.IsFalse(Invoke<bool>(renderer, "RenderEntityMirror",
                harness.Config, harness.FrameWidth, harness.FrameHeight, false));
            Assert.IsFalse(GetField<bool>(renderer, "faulted"));
''' + method[end:]
s = s[:a] + method + s[b:]
changes[p] = s

p = 'tests/VintageRTX.Test/DisplayShaderAssetTests.cs'
s = load(p, 'ea7bca0fdd52fe6d8450a42373ac601cf93d3100')
s = once(s, '''        StringAssert.Contains(fragment, "vec3 softLimitSpecularRadiance(");
        StringAssert.Contains(fragment, "float specularEnergyCeiling = max(");
        StringAssert.Contains(fragment, "1.0 - exp(-excess / shoulderRange)");
        StringAssert.Contains(fragment, "directSpecularRadiance = softLimitSpecularRadiance(");''', '''        // Numerical GPU tests qualify the real GGX lobe and footprint filter.
        // Do not require the removed empirical radiance clamp to be restored.
        StringAssert.Contains(fragment, "materialFresnel(f0, vh)");
        StringAssert.Contains(fragment, "vec3 directSpecularRadiance = voxelLighting.directSpecular");
        Assert.IsFalse(fragment.Contains("directSpecularRadiance = softLimitSpecularRadiance(",
            StringComparison.Ordinal), "Direct transport must not reintroduce an empirical energy ceiling.");''')
s = once(s, '''    /// pixel footprint and receive a continuous energy shoulder, while the''',
'''    /// pixel footprint without an empirical post-BRDF energy ceiling, while the''')
changes[p] = s

p = 'tests/VintageRTX.Test/PbrEntityRendererTests.cs'
s = load(p, '9d10667f3715dd090f96cd33132674c85c6c20d2')
s = once(s, '''        StringAssert.Contains(shader, "? -float(1 + roughnessBits * 32 + albedoBits) / 1025.0");''', '''        StringAssert.Contains(shader, "float packedSurface = -float(1 + roughnessBits * 32 + albedoBits) / 1025.0;");
        StringAssert.Contains(shader, "vintagertxUnlitAlbedo = vec4(unlitLinear, fragPosition.z);");
        StringAssert.Contains(shader, "outGNormal = vec4(viewShadingNormal, packedSurface);");
        StringAssert.Contains(shader, "outGNormal.a=0;");
        StringAssert.Contains(shader, "vintagertxUnlitAlbedo=vec4(0.0);");''')
s = once(s, '''        StringAssert.Contains(shader, "vec3 dynamicSurfaceAlbedo = authoredAlbedoLuminance >= 0.0");
        StringAssert.Contains(shader, "? dynamicSurfaceAlbedo");''', '''        // A valid exact albedo wins; otherwise a dynamic receiver uses its own
        // raster data and returns before the static voxel reconstruction branch.
        int helper = shader.IndexOf("vec3 resolveSurfaceBaseColor(", StringComparison.Ordinal);
        int exact = shader.IndexOf("rawAlbedoMatchesSurface(raw, position, packedAvailable)", helper, StringComparison.Ordinal);
        int dynamic = shader.IndexOf("if (dynamicSurface > 0.5)", helper, StringComparison.Ordinal);
        int voxel = shader.IndexOf("return material.a >= 0.05 ? reconstructSurfaceAlbedo", helper, StringComparison.Ordinal);
        Assert.IsTrue(helper >= 0 && exact > helper && dynamic > exact && voxel > dynamic);
        StringAssert.Contains(shader[dynamic..voxel], "authoredLuminance >= 0.0");
        StringAssert.Contains(shader[dynamic..voxel], ": sourceLinear;");''')
changes[p] = s

p = 'tests/VintageRTX.Test/LiquidShaderPhysicsTests.cs'
s = load(p, 'f432ce826b69cc4ae96580b150b301f6d064c4a1')
start = s.index('        StringAssert.Contains(shader, "float partialLiquidRecovery = clamp(");')
end = s.index('        StringAssert.Contains(shader, "* liquidInterfaceDepthVisibility;");', start)
s = s[:start] + '''        // Only the actual per-pixel liquid interface may receive the resolve.
        // The removed shoreline colour reconstruction copied neighbours over
        // partial silhouettes; its old implementation is not an acceptance oracle.
        StringAssert.Contains(shader, "float strictSupport = aboveWaterVisibility(");
        StringAssert.Contains(shader, "readGBufferTexel(gOpaqueDepth, uv).r");
        StringAssert.Contains(shader, "readGBufferTexel(gLiquidDepth, uv).r < 0.99999");
        StringAssert.Contains(shader, "waterEvidence *= strictSupport;");
        StringAssert.Contains(shader, "horizontalReflector *= strictSupport;");
        Assert.IsFalse(shader.Contains("float partialLiquidRecovery = clamp(", StringComparison.Ordinal));
        Assert.IsFalse(shader.Contains("vec3 recoveredShoreSource = mix(", StringComparison.Ordinal));
''' + s[end:]
s = once(s, '"texture(gOpaquePosition, fallbackUv).xyz"', '"readGBufferTexel(gOpaquePosition, fallbackUv).xyz"')
# Every exact geometry fetch is nearest/categorical; inspect any further old
# sampler contracts explicitly rather than changing numerical image thresholds.
changes[p] = s

p = 'tests/VintageRTX.Test/ShadowFilterPipelineTests.cs'
s = load(p, 'ccbd91208b960afbbdd5be47a14b0d7e4e1ff765')
a, b, method = body(s, 'public void TemporalHistorySnapsSubCentimetreLightJitterAndRejectsMotion()')
start = method.index('        CollectionAssert.AreEqual(')
end = method.index('        float[] moved', start)
method = method[:start] + '''        CollectionAssert.AreEqual(
            new float[] { 10.004f, 20.0f, 30.0f, 1.0f, -2.0f, 4.006f, 8.0f, 1.0f },
            jittered,
            "History eligibility must not snap or relocate a physical emitter.");

''' + method[end:]
method = method.replace('TemporalHistorySnapsSubCentimetreLightJitterAndRejectsMotion',
    'TemporalHistoryPreservesSubCentimetreLightPositionsAndRejectsMotion')
s = s[:a] + method + s[b:]
start = s.index('        StringAssert.Contains(shader, "if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)");')
end = s.index('        StringAssert.Contains(shader, "if (prefilteredShadowVisibility == 0 && !cameraAlignedLight)");', start)
s = s[:start] + '''        Assert.IsFalse(shader.Contains("geometryWasRepaired = true;", StringComparison.Ordinal),
            "Missing geometry must not be grown from neighbouring silhouettes.");
        string resolve = ShaderKernelBody(shader, "void resolveSurfaceShadow(");
        StringAssert.Contains(resolve, "planeError > tolerance * 3.0");
        StringAssert.Contains(resolve, "texelFetch(shadowPointHistoryA, pixel, 0)");
        StringAssert.Contains(resolve, "texelFetch(shadowPointHistoryB, pixel, 0)");
        StringAssert.Contains(resolve, "texelFetch(shadowSunHistory, pixel, 0).r");
        StringAssert.Contains(resolve, "traceRawPointShadowVisibilities(worldPosition, worldNormal, pointA, pointB)");
        StringAssert.Contains(resolve, "traceRawSunShadowVisibility(worldPosition, relativePosition, worldNormal)");
''' + s[end:]
s = once(s, '''        StringAssert.Contains(shader, "distance(lightPositionIntensity.xyz, floatingWorldOrigin) < 0.75");''', '''        Assert.IsFalse(shader.Contains("distance(lightPositionIntensity.xyz, floatingWorldOrigin) < 0.75", StringComparison.Ordinal),
            "Camera proximity alone must never bypass an emitter visibility query.");
        StringAssert.Contains(ShaderKernelBody(shader, "void traceRawPointShadowVisibilities("),
            "bool cameraAlignedLight = false;");''')
changes[p] = s

p = 'tests/VintageRTX.Test/VoxelSceneCoverageDeepTests.cs'
s = load(p, 'fd48a419d751f8da99433c9d6551776ac8397d86')
s = once(s, '''        Assert.AreEqual("unknown", fixture.Field<List<VoxelLight>>("buildLights")[1].Code);''', '''        Assert.AreEqual(1, fixture.Field<List<VoxelLight>>("buildLights").Count,
            "An emitter without a canonical code is rejected, not published as a fictitious second source.");
        Assert.AreEqual("game:lantern", fixture.Field<List<VoxelLight>>("buildLights")[0].Code);''')
changes[p] = s

# Record exact remaining lifecycle/precision kernels for the red/green investigation.
for file, signatures in {
    'src/VintageRTX/Rendering/VoxelScene.cs': ['private void OnBlockChanged(', 'private bool BlockEmitsLight('],
    'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs': ['internal static bool StabilizeShadowLightPositions('],
}.items():
    source = (ROOT / file).read_text(encoding='utf-8')
    for signature in signatures:
        a, b, method = body(source, signature)
        print('INSPECT', file, 'line', source[:a].count('\n') + 1, '\n' + method)
for path, source in changes.items():
    (ROOT / path).write_text(source, encoding='utf-8', newline='\n')
    print('UPDATED', path)
