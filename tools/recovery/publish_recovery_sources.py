#!/usr/bin/env python3
"""One-shot reviewed source migration. Not a build or runtime dependency.
All original blobs and anchors are checked before writing; changed files cause rejection.
"""
from pathlib import Path
import re
import json
from audit_patch import EXPECTED_BLOBS, SHADER, RENDERER, MIRROR, blob_sha, normalize, transform

ROOT = Path(__file__).resolve().parents[2]
MOD = 'src/VintageRTX/VintageRtxModSystem.cs'
CONFIG = 'src/VintageRTX/Configuration/VintageRtxConfig.cs'
STORE = 'src/VintageRTX/Configuration/ConfigStore.cs'
EXPECTED = {**EXPECTED_BLOBS, MOD: 'ebb0977460eac22bf75dee29bf2ec7aff8719501',
    CONFIG: '3c1a3c774bcc2c7a79987f001392b2a6fb7e7e47', STORE: '609c980668b204abfc149664af9b7d249f0c04bc'}


def replace(text, before, after):
    count = text.count(before)
    if count != 1:
        raise RuntimeError(f'Expected one exact anchor, got {count}: {before[:100]!r}')
    return text.replace(before, after, 1)


def function_span(text, name):
    matches = list(re.finditer(r'\b(?:bool|float|int|ReflectionResult)\s+' + re.escape(name) + r'\s*\(', text))
    if len(matches) != 1:
        raise RuntimeError(f'Expected one function definition: {name}')
    start = matches[0].start()
    opening = text.index('{', matches[0].end())
    depth = 1
    i = opening + 1
    while depth and i < len(text):
        depth += (text[i] == '{') - (text[i] == '}')
        i += 1
    if depth: raise RuntimeError(f'Unbalanced function: {name}')
    return start, i


def replace_function(text, name, replacement):
    a, b = function_span(text, name)
    return text[:a] + replacement.rstrip() + text[b:]


def main():
    texts = {}
    for relative, expected in EXPECTED.items():
        data = normalize((ROOT / relative).read_bytes())
        if blob_sha(data) != expected:
            raise RuntimeError(f'Not the reviewed base: {relative} ({blob_sha(data)})')
        texts[relative] = data.decode('utf-8')
    result = transform(texts, include_ssgi=True)
    shader = result[SHADER]
    kernel = (ROOT / 'tools/recovery/trace_kernel.glsl').read_text(encoding='utf-8')
    shader = replace_function(shader, 'traceFineBlockVisibility', kernel)
    shader = replace_function(shader, 'traceVoxelBounceSurface', '''bool traceVoxelBounceSurface(
    vec3 rayOrigin, vec3 rayDirection, out vec3 hitPosition, out vec3 hitNormal,
    out vec3 hitAlbedo, out float hitDistance)
{
    vec4 hitMaterial;
    int status = traceVoxelSurface(rayOrigin, rayDirection, voxelBounceDistance,
        voxelBounceSteps, hitDistance, hitNormal, hitMaterial);
    hitPosition = rayOrigin + rayDirection * hitDistance;
    hitAlbedo = hitMaterial.rgb;
    return status == 1;
}''')
    shader = replace_function(shader, 'traceCoarseBounceVisibility', '''float traceCoarseBounceVisibility(vec3 rayOrigin, vec3 rayTarget)
{
    // Reuse hierarchical occupancy instead of sparse probes that tunnel through walls.
    return traceVoxelVisibility(rayOrigin, rayTarget, -1);
}''')
    a, b = function_span(shader, 'traceVoxelReflection')
    reflection = shader[a:b]
    tail = reflection.index('    vec3 gridOrigin = rayOrigin - voxelOrigin;')
    reflection = reflection[:tail] + '''    float maximumDistance = reflectionDistance * mix(1.0, 0.52, roughness);
    float traveled;
    vec3 hitNormal;
    vec4 hitMaterial;
    int hitStatus = traceVoxelSurface(rayOrigin, rayDirection, maximumDistance,
        voxelReflectionSteps, traveled, hitNormal, hitMaterial);
    if (hitStatus != 1)
    {
        // Exhausted budget and unknown coverage have no fabricated geometry or radiance.
        return result;
    }
    vec3 hitPosition = rayOrigin + rayDirection * traveled;
    float distanceFade = 1.0 - smoothstep(maximumDistance * 0.55, maximumDistance, traveled);
    float grazingConfidence = 0.35
        + 0.65 * (1.0 - abs(dot(worldNormal, incidentDirection)));
    result.voxelColor = shadeVoxelReflectionHit(hitPosition, hitNormal, hitMaterial.rgb);
    result.voxelConfidence = clamp(
        distanceFade * grazingConfidence * (1.0 - roughness * 0.55), 0.0, 1.0);
    result.color = result.voxelColor;
    result.confidence = result.voxelConfidence;
    return result;
}'''
    shader = shader[:a] + reflection + shader[b:]
    shader = replace(shader,
        'vec3 metallicReflectance = max(surfaceAlbedo * 1.55, vec3(0.50));',
        'vec3 metallicReflectance = clamp(surfaceAlbedo, vec3(0.0), vec3(1.0));')
    shader = replace(shader, '''    // Dark raster albedo is not a valid conductor F0. Iron, steel and
    // other metals still reflect a broad environment lobe even when
    // their diffuse texture is nearly black; a bounded floor prevents
    // anvils from disappearing while retaining authored tint.''', '''    // The metalness base colour is a reflectance, never an artistic amplification.
    // Legacy specular gains elsewhere remain a separate BSDF migration; this prevents F0 > 1.''')
    shader = replace(shader, '''    reflectedColor += hitAlbedo
        * sunColorStrength.rgb
        * sunColorStrength.w
        * sunReceiver
        * 0.42;''', '''    float reflectedSunVisibility = sunReceiver > 0.0
        ? traceSunVisibility(hitPosition - floatingWorldOrigin,
            hitPosition + hitNormal * 0.02 + sunDirection * 0.005)
        : 0.0;
    reflectedColor += hitAlbedo
        * sunColorStrength.rgb
        * sunColorStrength.w
        * sunReceiver
        * reflectedSunVisibility
        * 0.42;''')
    shader = replace(shader, '''        reflectedColor += hitAlbedo
            * lightColorRadius.rgb
            * receiver
            * radiusFade
            * incidentRadiance
            * 0.75;''', '''        float reflectedLightVisibility = receiver > 0.0
            ? traceVoxelVisibility(hitPosition + hitNormal * 0.02 + lightDirection * 0.005,
                lightPositionIntensity.xyz, lightIndex)
            : 0.0;
        reflectedColor += hitAlbedo
            * lightColorRadius.rgb
            * receiver
            * reflectedLightVisibility
            * radiusFade
            * incidentRadiance
            * 0.75;''')
    result[SHADER] = shader
    renderer = result[RENDERER]
    renderer = replace(renderer, '''            int effectiveVoxelBounceRayCount = voxelBounceDiagnosticCapture
                ? Math.Max(1, config.VoxelBounceRayCount)
                : adaptiveQualityLevel switch''', '''            int effectiveVoxelBounceRayCount = adaptiveQualityLevel switch''')
    renderer = replace(renderer, '''            int effectiveReflectionSteps = reflectionDiagnosticCapture
                ? Math.Max(10, highQualityReflectionSteps)
                : adaptiveQualityLevel switch''', '''            int effectiveReflectionSteps = adaptiveQualityLevel switch''')
    renderer = replace(renderer, '''            int effectiveVoxelReflectionSteps = voxelReflectionDiagnosticCapture
                ? Math.Max(32, highQualityVoxelReflectionSteps)
                : adaptiveQualityLevel switch''', '''            int effectiveVoxelReflectionSteps = adaptiveQualityLevel switch''')
    for declaration in [
        '''            bool voxelReflectionDiagnosticCapture = captureFrame
                && effectiveDebugView == VintageRtxDebugView.VoxelReflection;
''',
        '''            bool reflectionDiagnosticCapture = captureFrame
                && effectiveDebugView is VintageRtxDebugView.Reflection
                    or VintageRtxDebugView.VoxelReflection;
''',
        '''            bool voxelBounceDiagnosticCapture = captureFrame
                && effectiveDebugView == VintageRtxDebugView.VoxelBounce;
''']:
        renderer = replace(renderer, declaration, '')
    for name in ['voxelReflectionDiagnosticCapture', 'reflectionDiagnosticCapture', 'voxelBounceDiagnosticCapture']:
        if name in renderer: raise RuntimeError(f'Unreviewed diagnostic consumer remains: {name}')
    result[RENDERER] = renderer

    config = result[CONFIG]
    defaults = dict(re.findall(r'public float (\w+) \{ get; set; \} = ([0-9.]+f);', config))
    for name, default in defaults.items():
        anchor = f'        {name} = Math.Clamp({name}, '
        if config.count(anchor) != 1: raise RuntimeError(f'Missing float clamp: {name}')
        config = config.replace(anchor, f'        {name} = Math.Clamp(float.IsFinite({name}) ? {name} : {default}, ', 1)
    result[CONFIG] = config

    store = replace(result[STORE], 'using Vintagestory.API.Common;', 'using Newtonsoft.Json;\nusing Vintagestory.API.Common;')
    store = replace(store, '    public VintageRtxConfig Current { get; private set; }', '''    public VintageRtxConfig Current { get; private set; }

    /// <summary>Persists a normalized detached candidate before replacing live configuration.</summary>
    /// <param name="draft">UI draft; never retained by reference.</param>
    /// <returns>The newly published configuration.</returns>
    /// <exception cref="InvalidOperationException">Future-schema data is protected against overwrites.</exception>
    public VintageRtxConfig Apply(VintageRtxConfig draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (futureSchemaReadOnly)
        {
            throw new InvalidOperationException("The configuration was written by a newer VintageRTX version.");
        }
        VintageRtxConfig candidate = JsonConvert.DeserializeObject<VintageRtxConfig>(
            JsonConvert.SerializeObject(draft))
            ?? throw new InvalidOperationException("Could not clone the configuration draft.");
        candidate.Clamp();
        candidate.SchemaVersion = VintageRtxConfig.CurrentSchemaVersion;
        api.StoreModConfig(candidate, FileName);
        Current = candidate;
        return candidate;
    }''')
    result[STORE] = store
    mod = replace(result[MOD], '    private ConfigStore? configStore;', '''    private ConfigStore? configStore;
    private GuiDialogVintageRtxSettings? settingsDialog;''')
    mod = replace(mod, '        RegisterCommands(clientApi);', '''        settingsDialog = new GuiDialogVintageRtxSettings(clientApi, configStore, () => renderer.ResetFault());
        clientApi.Input.RegisterHotKey("vintagertx-settings", "VintageRTX settings",
            GlKeys.R, HotkeyType.GUIOrOtherControls, altPressed: true, ctrlPressed: true);
        clientApi.Input.SetHotKeyHandler("vintagertx-settings", _ =>
        {
            settingsDialog?.Toggle();
            return true;
        });
        RegisterCommands(clientApi);''')
    mod = replace(mod, '''            .WithDescription("VintageRTX rendering controls")''', '''            .WithDescription("VintageRTX rendering controls")
            .BeginSubCommand("settings")
                .HandleWith(_ =>
                {
                    settingsDialog?.Toggle();
                    return TextCommandResult.Success();
                })
            .EndSubCommand()''')
    mod = replace(mod, '''    public override void Dispose()
    {''', '''    public override void Dispose()
    {
        settingsDialog?.Dispose();
        settingsDialog = null;
        api?.Input.SetHotKeyHandler("vintagertx-settings", _ => false);''')
    result[MOD] = mod

    translations = json.loads((ROOT / 'tools/recovery/settings_translations.json').read_text(encoding='utf-8'))
    for language, values in translations.items():
        relative = f'src/VintageRTX/assets/vintagertx/lang/{language}.json'
        path = ROOT / relative
        existing = json.loads(path.read_text(encoding='utf-8-sig')) if path.exists() else {}
        if any(key in existing for key in values):
            raise RuntimeError('Settings localization already exists; rebase manually.')
        existing.update(values)
        result[relative] = json.dumps(existing, ensure_ascii=False, indent=2) + '\n'

    # Publish ordinary source, not runtime/build-time substitutions.
    for relative, source in result.items():
        (ROOT / relative).write_text(source, encoding='utf-8', newline='\n')
        print(f'UPDATED {relative}')


if __name__ == '__main__':
    main()
