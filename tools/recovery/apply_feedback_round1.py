"""One-shot, exact-base source migration for player feedback round 1.
Never run from MSBuild or the game. CI compiles/tests the resulting ordinary sources before publish.
"""
from pathlib import Path
import hashlib
import re

ROOT = Path(__file__).resolve().parents[2]
BASE = {
 'src/VintageRTX/Rendering/VoxelScene.cs': '820eafed370bf27062684a2f0c021955310b027e',
 'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs': 'ce9fa1c4eeb726f8ce68c5bdcd16ebe17c29bec4',
 'src/VintageRTX/assets/vintagertx/shaders/display.frag': '95b11ac3366e4a41d28a6dd698db2d14b9c83494',
 'src/VintageRTX/assets/game/shaders/entityanimated.fsh': '7f660a86d5bae8e03740db0f79b7c923f88d5eba',
}

def blob(data):
    return hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()

def once(text, old, new):
    if text.count(old) != 1:
        raise ValueError(f'Expected one anchor ({text.count(old)} found): {old[:130]}')
    return text.replace(old, new, 1)

def span(text, signature):
    start = text.index(signature)
    opening = text.index('{', start)
    depth = 1
    i = opening + 1
    while depth:
        if i == len(text): raise ValueError('Unclosed function: ' + signature)
        depth += (text[i] == '{') - (text[i] == '}')
        i += 1
    return start, opening, i

def replace_function(text, signature, replacement):
    a, _, b = span(text, signature)
    return text[:a] + replacement + text[b:]

def insert_body(text, signature, addition):
    _, opening, _ = span(text, signature)
    return text[:opening + 1] + '\n' + addition + text[opening + 1:]

sources = {}
for path, expected in BASE.items():
    data = (ROOT / path).read_bytes()
    if blob(data) != expected: raise ValueError('Source revision mismatch: ' + path)
    sources[path] = data.decode('utf-8')

p = 'src/VintageRTX/Rendering/VoxelScene.cs'
s = sources[p]
s = once(s, '    private VoxelLight[] readyLights = Array.Empty<VoxelLight>();', '''    private VoxelLight[] readyLights = Array.Empty<VoxelLight>();
    private bool liveLightsUploadPending;
    private bool liveRadianceUploadPending;
    private bool radianceRefreshRequested;
    private long readyGeometryRevision;
    private Task<VoxelRadianceField>? radianceRefreshTask;
    private int radianceTaskGeneration;
    private long radianceTaskRevision;
''')
a, _, b = span(s, '    private void CollectLight(')
old = s[a:b]
start = old.index('        int rgb = ColorUtil.HsvToRgb(')
end = old.index('        CachedBlockOccupancy occupancy =', start)
old = old[:start] + '''        if (!EmitterAppearance.TryResolve(block.Code, block.Attributes, lightHsv,
            emitterPhotometryByCodeRoot, out EmitterAppearance appearance)) return;
        string blockCode = block.Code?.ToString() ?? "unknown";
        float red = appearance.Red, green = appearance.Green, blue = appearance.Blue;
        EmitterPhotometry photometry = appearance.Photometry;
''' + old[end:]
s = s[:a] + old + s[b:]
# BlockChanged may run during network/world work. Queue coordinates only; no world queries,
# mutable instance cache edits or full-volume rebuild just because a source changed.
a, _, b = span(s, '    private void OnBlockChanged(')
old = s[a:b]
old = once(old, '''        instanceMeshOccupancyByPosition.Remove(
            (position.X, position.Y, position.Z, position.dimension));''', '''        if (api.World.Player?.Entity is not { } player
            || position.dimension != player.Pos.Dimension) return;''')
start = old.index('        if (insideMainVolume)\n')
end = old.index('        dirtyBlocks.TryAdd(', start)
old = old[:start] + old[end:]
s = s[:a] + old + s[b:]
s = once(s, '''            UpdateReadyVoxel(position.X, position.Y, position.Z);
            UpdateReadyFluidSurface''', '''            instanceMeshOccupancyByPosition.Remove(
                (position.X, position.Y, position.Z, samplePosition.dimension));
            UpdateReadyVoxel(position.X, position.Y, position.Z);
            RefreshReadyEmitter(position.X, position.Y, position.Z);
            readyGeometryRevision++;
            radianceRefreshRequested = true;
            UpdateReadyFluidSurface''')
s = once(s, '''            ProcessDirtyBlocks();
            return;''', '''            ProcessDirtyBlocks();
            PollRadianceRefresh();
            return;''')
s = insert_body(s, '    private void BeginRebuild(', '''        liveLightsUploadPending = false;
        liveRadianceUploadPending = false;
        radianceRefreshRequested = false;
        readyGeometryRevision++;
''')
# The worker consumes only detached arrays and scalars, never the world or GL.
addition = '''    /// <summary>Refreshes the emitter table without rescanning any terrain, liquid or sun volume.</summary>
    private void RefreshReadyEmitter(int x, int y, int z)
    {
        if (x < originX || x >= originX + Width || y < originY || y >= originY + Height
            || z < originZ || z >= originZ + Depth) return;
        buildLights.RemoveAll(light => (int)MathF.Floor(light.X) == x
            && (int)MathF.Floor(light.Y) == y && (int)MathF.Floor(light.Z) == z);
        samplePosition.Set(x, y, z);
        Block solid = api.World.BlockAccessor.GetBlock(samplePosition, BlockLayersAccess.Solid);
        Block fluid = api.World.BlockAccessor.GetBlock(samplePosition, BlockLayersAccess.Fluid);
        CollectLight(solid, x, y, z);
        if (!ReferenceEquals(solid, fluid)) CollectLight(fluid, x, y, z);
        VoxelLight[] selected = buildLights.OrderByDescending(light => light.Score(api.World.Player.Entity.CameraPos))
            .ThenBy(light => light.X).ThenBy(light => light.Y).ThenBy(light => light.Z)
            .Take(MaximumLightCount).ToArray();
        if (!readyLights.SequenceEqual(selected))
        {
            readyLights = selected;
            liveLightsUploadPending = true;
        }
    }

    /// <summary>Publishes direct emitter/caster updates only into their matching geometry generation.</summary>
    internal bool TryConsumeLiveLights(int publishedGeneration, out VoxelLight[] lights)
    {
        lights = readyLights;
        if (building || !liveLightsUploadPending || generation != publishedGeneration) return false;
        liveLightsUploadPending = false;
        return true;
    }

    /// <summary>Publishes a completed indirect field only into the matching immutable generation.</summary>
    internal bool TryConsumeLiveRadiance(int publishedGeneration, out VoxelRadianceField field)
    {
        field = default;
        if (building || !liveRadianceUploadPending || generation != publishedGeneration) return false;
        field = new VoxelRadianceField(readyIrradiance, readyIrradianceDirection);
        liveRadianceUploadPending = false;
        return true;
    }

    /// <summary>Builds GI on detached data; rejects results superseded by edits or recentering.</summary>
    private void PollRadianceRefresh()
    {
        if (radianceRefreshTask is { IsCompleted: true })
        {
            if (radianceRefreshTask.IsCompletedSuccessfully)
            {
                if (radianceTaskGeneration == generation && radianceTaskRevision == readyGeometryRevision)
                {
                    VoxelRadianceField field = radianceRefreshTask.Result;
                    readyIrradiance = field.Irradiance;
                    readyIrradianceDirection = field.Direction;
                    liveRadianceUploadPending = true;
                }
            }
            else
            {
                api.Logger.Warning("[VintageRTX] Deferred irradiance refresh failed: {0}",
                    radianceRefreshTask.Exception?.GetBaseException().Message ?? "cancelled");
            }
            radianceRefreshTask = null;
        }
        if (!radianceRefreshRequested || radianceRefreshTask is not null || generation == 0) return;
        byte[] material = (byte[])readyVoxels.Clone();
        VoxelLight[] lights = (VoxelLight[])readyLights.Clone();
        int x = originX, y = originY, z = originZ;
        Vec3d camera = api.World.Player.Entity.CameraPos;
        float daylight = Math.Clamp((api.World.Calendar as IClientGameCalendar)?
            .GetDayLightStrength(camera.X, camera.Z) ?? 0f, 0f, 1f);
        Vec3f sky = new(0.0025f + daylight * 0.068f, 0.0045f + daylight * 0.096f, 0.0100f + daylight * 0.142f);
        radianceTaskGeneration = generation;
        radianceTaskRevision = readyGeometryRevision;
        radianceRefreshRequested = false;
        radianceRefreshTask = Task.Run(() => BuildRadianceField(material, lights, x, y, z, sky));
    }

'''
s = once(s, '    private void ProcessDirtyBlocks()', addition + '    private void ProcessDirtyBlocks()')
sources[p] = s

p = 'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs'
s = sources[p]
s = once(s, '    private readonly ICoreClientAPI api;', '''    private readonly ICoreClientAPI api;
    private EntityLightCollector? entityLightCollector;
    private readonly byte[] liveCasterUpload = new byte[VoxelScene.LightCasterVoxelCount * VoxelScene.MaximumLightCount];
''')
s = insert_body(s, '    private void BindVoxelUniforms(', '        UpdateLiveEmitterTextures();\n')
addition = '''    /// <summary>Updates small light/caster tables independently from full geometry publication.</summary>
    private void UpdateLiveEmitterTextures()
    {
        if (!voxelTextureReady) return;
        if (voxelScene.TryConsumeLiveLights(voxelSnapshot.Generation, out VoxelLight[] lights))
        {
            voxelSnapshot = voxelSnapshot with { Lights = lights };
            Array.Clear(liveCasterUpload);
            for (int i = 0; i < Math.Min(lights.Length, VoxelScene.MaximumLightCount); i++)
                if (lights[i].CasterMask.Length == VoxelScene.LightCasterVoxelCount)
                    Array.Copy(lights[i].CasterMask, 0, liveCasterUpload, i * VoxelScene.LightCasterVoxelCount, VoxelScene.LightCasterVoxelCount);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.ActiveTexture(TextureUnit.Texture6);
            GL.BindTexture(TextureTarget.Texture3D, voxelLightCasterTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                VoxelScene.LightCasterScale, VoxelScene.LightCasterScale,
                VoxelScene.LightCasterScale * VoxelScene.MaximumLightCount,
                PixelFormat.Red, PixelType.UnsignedByte, liveCasterUpload);
            temporalHistoryValid = false;
            shadowHistoryValid = false;
        }
        if (voxelScene.TryConsumeLiveRadiance(voxelSnapshot.Generation, out VoxelRadianceField field))
        {
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.ActiveTexture(TextureUnit.Texture9);
            GL.BindTexture(TextureTarget.Texture3D, voxelIrradianceTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                voxelSnapshot.Width, voxelSnapshot.Height, voxelSnapshot.Depth,
                PixelFormat.Rgb, PixelType.UnsignedByte, field.Irradiance);
            GL.ActiveTexture(TextureUnit.Texture10);
            GL.BindTexture(TextureTarget.Texture3D, voxelIrradianceDirectionTexture);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                voxelSnapshot.Width, voxelSnapshot.Height, voxelSnapshot.Depth,
                PixelFormat.Rgb, PixelType.UnsignedByte, field.Direction);
            temporalHistoryValid = false;
        }
    }

'''
s = once(s, '    private void BindVoxelUniforms(', addition + '    private void BindVoxelUniforms(')
# Keep source geometry moving, instead of snapping positions to a retained temporal reference.
for line in [
 '            currentPositions[currentOffset] = previousPositions[previousOffset];',
 '            currentPositions[currentOffset + 1] = previousPositions[previousOffset + 1];',
 '            currentPositions[currentOffset + 2] = previousPositions[previousOffset + 2];']:
    s = once(s, line, '')
s = once(s, '            shader.Uniform("temporalBlend", temporalBlend);', '''            // Final RGB has no previous-position/object contract. Retain only independently
            // validated shadow history; do not ghost moving geometry or reflection silhouettes.
            shader.Uniform("temporalBlend", 0.0f);''')
# Do not change the actual set of local lights as quality tiers transition.
pattern = r'            int maximumVoxelLights = adaptiveQualityLevel switch\s*\{.*?\n            \};'
s, n = re.subn(pattern, '            int maximumVoxelLights = VoxelScene.MaximumLightCount;', s, count=1, flags=re.S)
if n != 1: raise ValueError('maximumVoxelLights anchor')
a, _, b = span(s, '    private int CopyDynamicPointLights(')
method = s[a:b]
start = method.index('        int candidateCount = 0;')
end = method.index('        Span<int> candidateSourceIndices', start)
method = method[:start] + '''        int candidateCount = 0;
        entityLightCollector ??= new EntityLightCollector(api);
        IReadOnlyList<TrackedEntityLight> entities = entityLightCollector.Collect(originX, originY, originZ, config.PointLightRadius);
        HashSet<int> matched = new();
        Dictionary<int, VoxelLight> entityDefinitions = new();
        foreach (TrackedEntityLight entity in entities) entityDefinitions[entity.SourceIndex] = entity.Light;
        Array.Clear(selectedDynamicLightViewPositions);
        for (int index = 0; index < Math.Min(availableCount, dynamicLightCandidates.Length); index++)
        {
            int offset = index * 3;
            float r = pointLightColors[offset], g = pointLightColors[offset + 1], b = pointLightColors[offset + 2];
            float range = MathF.Sqrt(r * r + g * g + b * b);
            if (!float.IsFinite(range) || range < 0.25f) continue;
            float vx = pointLights[offset], vy = pointLights[offset + 1], vz = pointLights[offset + 2];
            if (!float.IsFinite(vx + vy + vz)) continue;
            TransformViewToWorld(vx, vy, vz, originX, originY, originZ, out float wx, out float wy, out float wz);
            TrackedEntityLight? owner = null;
            double closest = double.PositiveInfinity;
            foreach (TrackedEntityLight entity in entities)
            {
                if (matched.Contains(entity.SourceIndex)) continue;
                VoxelLight light = entity.Light;
                double dx = wx - light.X, dy = wy - light.Y, dz = wz - light.Z;
                double distanceSquared = dx * dx + dy * dy + dz * dz;
                // The public light arrays have no owner IDs. This one-to-one association only
                // substitutes photometry/identity; unmatched entity sources remain explicit.
                double tolerance = entity.LocalPlayer ? 2.0 : 0.65;
                if (distanceSquared < tolerance * tolerance && distanceSquared < closest)
                { closest = distanceSquared; owner = entity; }
            }
            if (owner is TrackedEntityLight known)
            {
                matched.Add(known.SourceIndex);
                AddEntity(known, wx, wy, wz, vx, vy, vz);
            }
            else
            {
                dynamicLightCandidates[candidateCount++] = new DynamicLightCandidate(index, vx, vy, vz, wx, wy, wz,
                    r / range, g / range, b / range, Math.Min(range, config.PointLightRadius),
                    EmitterPhotometry.CandelaAtCutoffRange(range, EmitterPhotometry.DefaultCutoffIlluminanceLux),
                    range / (1f + (vx * vx + vy * vy + vz * vz) * 0.35f));
            }
        }
        foreach (TrackedEntityLight entity in entities.OrderByDescending(e => e.Light.Intensity /
            (1.0 + Math.Pow(e.Light.X - originX, 2) + Math.Pow(e.Light.Y - originY, 2) + Math.Pow(e.Light.Z - originZ, 2))))
        {
            if (matched.Contains(entity.SourceIndex) || candidateCount >= dynamicLightCandidates.Length) continue;
            VoxelLight light = entity.Light;
            float x = (float)(light.X - originX), y = (float)(light.Y - originY), z = (float)(light.Z - originZ);
            float vx = viewMatrix[0] * x + viewMatrix[4] * y + viewMatrix[8] * z + viewMatrix[12];
            float vy = viewMatrix[1] * x + viewMatrix[5] * y + viewMatrix[9] * z + viewMatrix[13];
            float vz = viewMatrix[2] * x + viewMatrix[6] * y + viewMatrix[10] * z + viewMatrix[14];
            AddEntity(entity, light.X, light.Y, light.Z, vx, vy, vz);
        }
        availableDynamicLightCount = candidateCount;

        void AddEntity(TrackedEntityLight entity, float x, float y, float z, float vx, float vy, float vz)
        {
            VoxelLight light = entity.Light;
            float range = Math.Min(light.TraceRadiusMetres(), config.PointLightRadius);
            dynamicLightCandidates[candidateCount++] = new DynamicLightCandidate(entity.SourceIndex,
                vx, vy, vz, x, y, z, light.Red, light.Green, light.Blue, range, light.Intensity,
                range * light.Intensity / (1f + (vx * vx + vy * vy + vz * vz) * 0.35f));
        }

''' + method[end:]
method = once(method, '''            voxelLightPhotometry[copiedOffset + 3] = 0.0f;''', '''            voxelLightPhotometry[copiedOffset + 3] = 0.0f;
            if (entityDefinitions.TryGetValue(candidate.SourceIndex, out VoxelLight definition))
            {
                voxelLightPhotometry[copiedOffset] = definition.SourceHalfWidthMetres;
                voxelLightPhotometry[copiedOffset + 1] = definition.SourceHalfHeightMetres;
                voxelLightPhotometry[copiedOffset + 2] = definition.CutoffIlluminanceLux;
                voxelLightPhotometry[copiedOffset + 3] = 1.0f;
            }''')
s = s[:a] + method + s[b:]
s = insert_body(s, '    private int FindDuplicateDynamicLight(', '''        // Explicit entities may legitimately stand beside a placed lamp. Never collapse them
        // using the legacy proximity-only static/engine-light association below.
''')
a, _, b = span(s, '    private int FindDuplicateDynamicLight(')
method = s[a:b]
method = once(method, '''        for (int index = 0; index < lightCount; index++)
        {''', '''        for (int index = 0; index < lightCount; index++)
        {
            if (index < lastSelectedDynamicSourceCount && lastSelectedDynamicSourceIndices[index] >= 1_000_000) continue;''')
s = s[:a] + method + s[b:]
sources[p] = s

p = 'src/VintageRTX/assets/game/shaders/entityanimated.fsh'
s = sources[p]
s = once(s, '''    float packedSurface = vintagertxEntitySurface
        ? -float(1 + roughnessBits * 32 + albedoBits) / 1025.0
        : 0.0;''', '''    // Entity identity and unlit luminance exist even when this skin has no PBR sidecars.
    // The first-person depth-offset branch below still clears this marker deliberately.
    float packedSurface = -float(1 + roughnessBits * 32 + albedoBits) / 1025.0;''')
sources[p] = s

p = 'src/VintageRTX/assets/vintagertx/shaders/display.frag'
s = sources[p]
helper = '''
// Packed surface payloads and visibility guides must never be interpolated across objects.
vec4 readGBufferTexel(sampler2D source, vec2 coord)
{
    ivec2 size = textureSize(source, 0);
    ivec2 pixel = clamp(ivec2(floor(coord * vec2(size))), ivec2(0), size - ivec2(1));
    return texelFetch(source, pixel, 0);
}

// This is the above-water branch only; submerged optics must have their own medium/IOR contract.
float aboveWaterVisibility(float cameraY, float surfaceY, float rayY, float surfaceDepth, float opaqueDepth)
{
    if (isnan(cameraY + surfaceY + rayY + surfaceDepth + opaqueDepth)
        || isinf(cameraY + surfaceY + rayY + surfaceDepth + opaqueDepth)) return 0.0;
    return cameraY > surfaceY + 0.0001 && rayY < -0.00001
        && surfaceDepth >= 0.0 && surfaceDepth < 1.0 && surfaceDepth < opaqueDepth ? 1.0 : 0.0;
}
'''
s = once(s, 'vec3 srgbToLinear(vec3 color)', helper + '\nvec3 srgbToLinear(vec3 color)')
s, count = re.subn(r'texture\(\s*(gNormal|gPosition|gDirectPosition|gOpaquePosition|gOpaqueDepth|gLiquidDepth)\s*,', r'readGBufferTexel(\1,', s)
if count < 10: raise ValueError('G-buffer readers changed unexpectedly')
# Never create geometry by averaging neighbours at a silhouette; this also averaged packed IDs.
a = s.index('    if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)')
opening = s.index('{', a); depth = 1; end = opening + 1
while depth:
    depth += (s[end] == '{') - (s[end] == '}'); end += 1
s = s[:a] + '    // Missing geometry remains missing; no outline-growing G-buffer dilation.\n' + s[end:]
pattern = r'bool cameraAlignedLight = voxelLightCasterLayer\[lightIndex\] < -0\.5\s*&& distance\(lightPositionIntensity.xyz, floatingWorldOrigin\) < 0\.75;'
s, n = re.subn(pattern, 'bool cameraAlignedLight = false; // Every emitter traces world visibility, including held lights.', s)
if n != 2: raise ValueError(f'Camera-light shortcuts: expected 2, found {n}')
s = once(s, '        + float(lightIndex) * 1.32471795724', '        + 0.0 // A light-slot reorder must not rotate its finite-source quadrature.')
# Final above-water visibility is checked after dynamic displacement, against immutable opaque depth.
anchor = '        liquidSurfaceNormal = profileDrivenLiquidNormal('
s = once(s, anchor, '''        vec4 interfaceClip = projection * viewMatrix
            * vec4(planarWorldPosition - floatingWorldOrigin, 1.0);
        float strictSurfaceDepth = interfaceClip.w > 0.00001
            ? interfaceClip.z / interfaceClip.w * 0.5 + 0.5 : 2.0;
        float strictOpaqueDepth = opaqueDepthEnabled != 0
            ? readGBufferTexel(gOpaqueDepth, uv).r : 1.0;
        float strictSupport = aboveWaterVisibility(cameraWorldPosition.y, planarFluidSurfaceWorldY,
            cameraRayWorld.y, strictSurfaceDepth, strictOpaqueDepth);
        if (opaqueDepthEnabled == 0)
            strictSupport *= surfaceDistance > 0.0 && surfaceDistance < opaqueDistance ? 1.0 : 0.0;
        // A column is not proof that a liquid face exists at this pixel. Do not copy a neighbouring
        // water pixel over an opaque silhouette. Containers retain their explicit surface contract.
        if (liquidDepthEnabled != 0 && containedSurfaceEvidence <= 0.001)
            strictSupport *= readGBufferTexel(gLiquidDepth, uv).r < 0.99999 ? 1.0 : 0.0;
        waterEvidence *= strictSupport;
        result.waterEvidence = waterEvidence;
        horizontalReflector *= strictSupport;
''' + anchor)
# No synthetic scene colour is allowed to repair liquid edges over real foreground pixels.
s = once(s, '''    float partialLiquidRecovery = clamp(
        reflection.liquidPartialGeometryFace,
        0.0,
        1.0);''', '''    // The underlying engine composite is authoritative where no real interface was proved.
    // Old shoreline recovery copied colour from other pixels through solid silhouettes.
    float partialLiquidRecovery = 0.0;''')
# A confirmed mirror hit needs no additional arbitrary blue sky wash.
s = once(s, '''        result.color = mix(
            result.color,
            reflectedSky,
            unresolvedEnvironmentBlend);''', '''        // Sky is already the fallback for unsupported samples above; never add it twice
        // around the boundary of a resolved reflected object.
''')
# No final-colour history until valid geometry/motion reprojection exists; shadow history is separate.
s = once(s, '''    float surfaceTemporalBlend = temporalBlend
        * (1.0 - smoothstep(0.001, 0.02, planarResponse));''', '''    float surfaceTemporalBlend = 0.0; // No same-UV RGB history across moving objects/reflections.''')
# Full-resolution geometry-guided upsampling. If the reduced mask has no matching receiver,
# trace this edge pixel instead of importing an adjacent surface's light or shadow.
helper = '''
void resolveSurfaceShadow(vec3 centerPosition, vec3 centerNormal, vec3 geometricViewNormal,
    out vec4 pointA, out vec4 pointB, out float sunlight)
{
    ivec2 size = textureSize(shadowPointHistoryA, 0);
    vec2 grid = uv * vec2(size) - vec2(0.5);
    ivec2 basePixel = ivec2(floor(grid));
    vec2 fraction = fract(grid);
    pointA = vec4(0.0); pointB = vec4(0.0); sunlight = 0.0;
    float weightSum = 0.0;
    for (int i = 0; i < 4; ++i)
    {
        ivec2 corner = ivec2(i & 1, i >> 1);
        ivec2 pixel = clamp(basePixel + corner, ivec2(0), size - ivec2(1));
        vec2 sampleUv = (vec2(pixel) + vec2(0.5)) / vec2(size);
        vec3 position = readGBufferTexel(gPosition, sampleUv).xyz;
        vec3 normal = readGBufferTexel(gNormal, sampleUv).xyz;
        if (dot(position, position) < 0.0001 || dot(normal, normal) < 0.01) continue;
        float planeError = abs(dot(geometricViewNormal, position - centerPosition));
        float tolerance = max(0.015, abs(centerPosition.z) * 0.0005);
        float normalAgreement = dot(centerNormal, normalize(normal));
        if (planeError > tolerance * 3.0 || normalAgreement < 0.6) continue;
        vec2 w = mix(vec2(1.0) - fraction, fraction, vec2(corner));
        float weight = w.x * w.y * exp(-planeError / tolerance) * smoothstep(0.6, 0.95, normalAgreement);
        pointA += texelFetch(shadowPointHistoryA, pixel, 0) * weight;
        pointB += texelFetch(shadowPointHistoryB, pixel, 0) * weight;
        sunlight += texelFetch(shadowSunHistory, pixel, 0).r * weight;
        weightSum += weight;
    }
    if (weightSum > 0.05)
    {
        pointA /= weightSum; pointB /= weightSum; sunlight /= weightSum;
    }
    else
    {
        vec3 relativePosition = (inverseViewMatrix * vec4(centerPosition, 1.0)).xyz;
        vec3 worldPosition = relativePosition + floatingWorldOrigin;
        vec3 worldNormal = normalize(mat3(inverseViewMatrix) * geometricViewNormal);
        traceRawPointShadowVisibilities(worldPosition, worldNormal, pointA, pointB);
        sunlight = traceRawSunShadowVisibility(worldPosition, relativePosition, worldNormal);
    }
}

'''
s = once(s, 'void main()\n{', helper + 'void main()\n{')
old = '''                        resolvedPointVisibilityA = clamp(
                            texture(shadowPointHistoryA, uv),
                            0.0,
                            1.0);
                        resolvedPointVisibilityB = clamp(
                            texture(shadowPointHistoryB, uv),
                            0.0,
                            1.0);
                        resolvedSunVisibility = clamp(
                            texture(shadowSunHistory, uv).r,
                            0.0,
                            1.0);'''
s = once(s, old, '''                        resolveSurfaceShadow(position, normal,
                            normalize(mat3(viewMatrix) * worldGeometricNormal),
                            resolvedPointVisibilityA, resolvedPointVisibilityB, resolvedSunVisibility);''')
sources[p] = s

# All preconditions and transformations must succeed before any original source is overwritten.
for path, content in sources.items():
    (ROOT / path).write_text(content, encoding='utf-8', newline='\n')
    print('UPDATED', path, blob(content.encode()))
