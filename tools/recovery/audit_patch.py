"""Checked mirror-depth and SSGI migration; never loaded by the mod or the build."""
from dataclasses import dataclass
import hashlib

MIRROR = 'src/VintageRTX/Rendering/EntityMirrorProjection.cs'
RENDERER = 'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs'
SHADER = 'src/VintageRTX/assets/vintagertx/shaders/display.frag'
EXPECTED_BLOBS = {
    MIRROR: '0ada90e13d2e405f87595223cbf9589f6b68aa5f',
    RENDERER: '9f82f0f624836260ffff033d22b2c1d7eb30aea5',
    SHADER: '6364aae6f952afb743fbc09727b1bad904b812fc',
}


def normalize(data):
    return data.replace(b'\r\n', b'\n')


def blob_sha(data):
    return hashlib.sha1(b'blob ' + str(len(data)).encode('ascii') + b'\0' + data).hexdigest()


def transform(texts, include_ssgi=True):
    edits = [
        (MIRROR, '    private readonly double[] obliqueProjectionMatrix = new double[16];', '''    private readonly double[] obliqueProjectionMatrix = new double[16];
    /// <summary>Projection shared by all producers of the current mirror depth.</summary>
    private readonly float[] mirrorDepthProjectionMatrix = new float[16];
    /// <summary>Inverse paired with the current mirror depth projection.</summary>
    private readonly float[] inverseMirrorDepthProjectionMatrix = new float[16];'''),
        (MIRROR, '    internal int DepthTextureId => depthTextureId;', '''    internal int DepthTextureId => depthTextureId;

    /// <summary>
    /// Gets the inverse projection paired with DepthTextureId after successful Render.
    /// The ordinary inverse view reconstructs reflected geometry, not the original source point.
    /// </summary>
    internal float[] InverseDepthProjectionMatrix => inverseMirrorDepthProjectionMatrix;'''),
        (MIRROR, '''            obliqueProjectionMatrix,
            inverseMirrorViewScratch,
            inverseProjectionScratch);
        if (mirrorProjectionReady)''', '''            obliqueProjectionMatrix,
            inverseMirrorViewScratch,
            inverseProjectionScratch);

        // The replay and the projected supplement write one depth attachment.
        // A failed oblique construction retains a consistent ordinary fallback pair.
        if (!mirrorProjectionReady)
        {
            if (projectionMatrix.Length < 16)
            {
                throw new ArgumentException("A complete fallback projection is required.", nameof(projectionMatrix));
            }
            for (int index = 0; index < 16; index++)
            {
                obliqueProjectionMatrix[index] = projectionMatrix[index];
            }
        }
        if (Mat4d.Invert(inverseProjectionScratch, obliqueProjectionMatrix) is null)
        {
            throw new InvalidOperationException("The mirror depth projection is singular.");
        }
        for (int index = 0; index < 16; index++)
        {
            float forward = (float)obliqueProjectionMatrix[index];
            float backward = (float)inverseProjectionScratch[index];
            if (!float.IsFinite(forward) || !float.IsFinite(backward))
            {
                throw new InvalidOperationException("The mirror depth matrices are not finite.");
            }
            mirrorDepthProjectionMatrix[index] = forward;
            inverseMirrorDepthProjectionMatrix[index] = backward;
        }
        if (mirrorProjectionReady)'''),
        (MIRROR, '''            terrainPositionTextureId,
            projectionMatrix,
            viewMatrix,''', '''            terrainPositionTextureId,
            mirrorDepthProjectionMatrix,
            viewMatrix,'''),
        (MIRROR, '''                terrainPositionTextureId,
                projectionMatrix,
                viewMatrix,''', '''                terrainPositionTextureId,
                mirrorDepthProjectionMatrix,
                viewMatrix,'''),
        (RENDERER, '            shader.Uniform("entityMirrorEnabled", entityMirrorReady ? 1 : 0);', '''            shader.Uniform("entityMirrorEnabled", entityMirrorReady ? 1 : 0);
            shader.UniformMatrix(
                "inverseEntityMirrorProjection",
                entityMirrorReady
                    ? entityMirrorProjection.InverseDepthProjectionMatrix
                    : inverseProjectionMatrix);'''),
        (SHADER, 'uniform mat4 inverseProjection;', '''uniform mat4 inverseProjection;
// Inverse of the active mirror projection, which may have an oblique near plane.
uniform mat4 inverseEntityMirrorProjection;'''),
        (SHADER, '''            vec4 reflectedEntityViewHomogeneous = inverseProjection
                * reflectedEntityClipPosition;''', '''            vec4 reflectedEntityViewHomogeneous = inverseEntityMirrorProjection
                * reflectedEntityClipPosition;''')]
    if include_ssgi:
        edits.extend([
            (SHADER, '        float rayAngle = (float(rayIndex) + 0.5) * 1.57079632679;', '''        // Preserve the first four directions, then interleave four distinct azimuths.
        int quadrant = rayIndex % 4;
        int layer = rayIndex / 4;
        float rayAngle = (float(quadrant) + 0.5) * 1.57079632679
            + float(layer) * 0.78539816339;'''),
            (SHADER, '            result.indirect += sampleReflectionSource(hitUv) * confidence;',
                '            result.indirect += srgbToLinear(sampleReflectionSource(hitUv)) * confidence;'),
            (SHADER, '''        + srgbToLinear(lighting.indirect)
            * indirectLightStrength''', '''        + lighting.indirect
            * indirectLightStrength'''),
            (SHADER, '        outColor = vec4(lighting.indirect + vec3(lighting.occlusion * 0.35), 1.0);', '''        outColor = vec4(
            linearToSrgb(lighting.indirect) + vec3(lighting.occlusion * 0.35),
            1.0);''')])
    changed = dict(texts)
    for path, before, after in edits:
        count = changed[path].count(before)
        if count != 1:
            raise RuntimeError(f'Expected one anchor in {path}, got {count}: {before[:80]!r}')
        changed[path] = changed[path].replace(before, after, 1)
    return changed
