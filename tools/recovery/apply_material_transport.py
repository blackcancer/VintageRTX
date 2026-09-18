"""Exact-base material transport migration; not executed by MSBuild or the game.
Adds raw opaque albedo, a shared material-aware GGX evaluation and linear secondary shading.
"""
from pathlib import Path
import hashlib

ROOT=Path(__file__).resolve().parents[2]
BASE={
 'src/VintageRTX/Rendering/ReflectionSourceCaptureRenderer.cs':{'cfd826c806ad81262d5caa3233d03316119f494c'},
 'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs':{'e393d6cd42cb4dcae8c97b22be72983586feda00'},
 'src/VintageRTX/assets/game/shaders/chunkopaque.fsh':{'f6e505b8ea492b149348e7662e70902eb29a3326'},
 'src/VintageRTX/assets/game/shaders/entityanimated.fsh':{'cfb0305f184d11cda9ede4c3e0c148f79c8909bf'},
 'src/VintageRTX/assets/vintagertx/shaders/display.frag':{'bc4a1d6bdc9532fc18a28ac8d4582a9894e0e95e'},
}
def blob(data): return hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
def once(s,old,new):
    if s.count(old)!=1: raise ValueError(f'Expected one anchor ({s.count(old)}): {old[:160]}')
    return s.replace(old,new,1)
def span(s,signature):
    a=s.index(signature); o=s.index('{',a); i=o+1; n=1
    while n:
        n+=(s[i]=='{')-(s[i]=='}'); i+=1
    return a,o,i
sources={}
for path,expected in BASE.items():
    data=(ROOT/path).read_bytes()
    if blob(data) not in expected: raise ValueError('Unexpected source identity: '+path+' '+blob(data))
    sources[path]=data.decode('utf-8')

p='src/VintageRTX/Rendering/ReflectionSourceCaptureRenderer.cs';s=sources[p]
s=once(s,'    private readonly ICoreClientAPI api;', '''    private readonly ICoreClientAPI api;
    private readonly RawAlbedoCapture? rawAlbedo;
    /// <summary>Unlit RGB captured in the same opaque transaction, or zero on unsupported paths.</summary>
    internal int RawAlbedoTextureId => ready ? rawAlbedo?.TextureId ?? 0 : 0;''')
s=once(s,'''    public ReflectionSourceCaptureRenderer(ICoreClientAPI api)
    {
        this.api = api;
    }''', '''    public ReflectionSourceCaptureRenderer(ICoreClientAPI api, RawAlbedoCapture? rawAlbedo = null)
    {
        this.api = api;
        this.rawAlbedo = rawAlbedo;
    }''')
s=once(s,'''            enabled = value;
            if (!value)''', '''            enabled = value;
            if (rawAlbedo is not null) rawAlbedo.Enabled = value;
            if (!value)''')
s=once(s,'''        long diagnosticStart = cpuDiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0L;''', '''        // Detach the extra material target before any local-player draw is replayed.
        rawAlbedo?.EndFrame();
        long diagnosticStart = cpuDiagnosticsEnabled ? Stopwatch.GetTimestamp() : 0L;''')
sources[p]=s

p='src/VintageRTX/Rendering/FilmicDisplayRenderer.cs';s=sources[p]
s=once(s,'    private readonly ReflectionSourceCaptureRenderer reflectionSourceCapture;', '''    private readonly ReflectionSourceCaptureRenderer reflectionSourceCapture;
    private readonly RawAlbedoCapture rawAlbedoCapture;''')
s=once(s,'''        reflectionSourceCapture = new ReflectionSourceCaptureRenderer(api);''', '''        rawAlbedoCapture = new RawAlbedoCapture(api);
        reflectionSourceCapture = new ReflectionSourceCaptureRenderer(api, rawAlbedoCapture);
        api.Event.RegisterRenderer(rawAlbedoCapture, EnumRenderStage.Opaque, "vintagertx-raw-albedo");''')
s=once(s,'''            shader = CreateShader();''', '''            rawAlbedoCapture.ReloadShader();
            shader = CreateShader();''')
a,o,b=span(s,'    public void Dispose()')
s=s[:o+1]+'''
        api.Event.UnregisterRenderer(rawAlbedoCapture, EnumRenderStage.Opaque);
        rawAlbedoCapture.Dispose();
'''+s[o+1:]
a,o,b=span(s,'    private void RenderDisplayPass(')
method=s[a:b]
method=once(method,'''        GlState state = GlState.Capture();''', '''        GlState state = GlState.Capture();
        GL.ActiveTexture(TextureUnit.Texture28);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousRawAlbedoBinding);
        GL.ActiveTexture((TextureUnit)state.ActiveTexture);''')
method=once(method,'''            shader.BindTexture2D("sourceColor", sourceColorTexture, 0);''', '''            shader.BindTexture2D("sourceColor", sourceColorTexture, 0);
            int rawAlbedoTexture = reflectionSourceCapture.RawAlbedoTextureId;
            shader.BindTexture2D("gUnlitAlbedo", rawAlbedoTexture > 0 ? rawAlbedoTexture : sourceColorTexture, 28);
            shader.Uniform("rawAlbedoEnabled", rawAlbedoTexture > 0 ? 1 : 0);''')
method=once(method,'''            state.Restore();''', '''            GL.ActiveTexture(TextureUnit.Texture28);
            GL.BindTexture(TextureTarget.Texture2D, previousRawAlbedoBinding);
            state.Restore();''')
s=s[:a]+method+s[b:];sources[p]=s

p='src/VintageRTX/assets/game/shaders/chunkopaque.fsh';s=sources[p]
s=once(s,'layout(location = 3) out vec4 outGPosition;', '''layout(location = 3) out vec4 outGPosition;
layout(location = 4) out vec4 vintagertxUnlitAlbedo;''')
s=once(s,'''    float unlitLuminance = dot(unlitLinear, vec3(0.2126, 0.7152, 0.0722));''', '''    // Actual colour-mapped linear texel before raster illumination; alpha owns its view Z.
    vintagertxUnlitAlbedo = vec4(unlitLinear, camPos.z);
    float unlitLuminance = dot(unlitLinear, vec3(0.2126, 0.7152, 0.0722));''')
sources[p]=s
p='src/VintageRTX/assets/game/shaders/entityanimated.fsh';s=sources[p]
s=once(s,'    layout(location = 3) out vec4 outGPosition;', '''    layout(location = 3) out vec4 outGPosition;
    layout(location = 4) out vec4 vintagertxUnlitAlbedo;''')
s=once(s,'''    float unlitLuminance = dot(unlitLinear, vec3(0.2126, 0.7152, 0.0722));''', '''    vintagertxUnlitAlbedo = vec4(unlitLinear, fragPosition.z);
    float unlitLuminance = dot(unlitLinear, vec3(0.2126, 0.7152, 0.0722));''')
s=once(s,'''        outGNormal.a=0;''', '''        outGNormal.a=0;
        vintagertxUnlitAlbedo=vec4(0.0);''');sources[p]=s

p='src/VintageRTX/assets/vintagertx/shaders/display.frag';s=sources[p]
s=once(s,'uniform sampler2D sourceColor;', '''uniform sampler2D sourceColor;
uniform sampler2D gUnlitAlbedo;
uniform int rawAlbedoEnabled;''')
# GGX height-correlated Smith visibility includes N.L but does not clamp the pointwise BRDF.
# It accepts actual F0 BEFORE evaluation, rather than tinting a dielectric result afterwards.
a,o,b=span(s,'vec3 evaluateDirectSpecular(')
s=s[:a]+'''vec3 materialFresnel(vec3 f0, float cosine)
{
    vec3 reflectance = clamp(f0, vec3(0.0), vec3(1.0));
    float grazing = pow(1.0 - clamp(cosine, 0.0, 1.0), 5.0);
    return reflectance + (vec3(1.0) - reflectance) * grazing;
}

vec3 evaluateDirectSpecular(
    vec3 worldNormal, vec3 viewDirection, vec3 lightDirection, float roughness, vec3 f0)
{
    float nv = dot(worldNormal, viewDirection);
    float nl = dot(worldNormal, lightDirection);
    vec3 halfVector = viewDirection + lightDirection;
    float halfLengthSquared = dot(halfVector, halfVector);
    if (nv <= 0.0 || nl <= 0.0 || halfLengthSquared <= 1e-12) return vec3(0.0);
    vec3 h = halfVector * inversesqrt(halfLengthSquared);
    float nh = clamp(dot(worldNormal, h), 0.0, 1.0);
    float vh = clamp(dot(viewDirection, h), 0.0, 1.0);
    float alpha = max(roughness * roughness, 0.0025);
    float a2 = alpha * alpha;
    float denominator = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / max(3.141592653589793 * denominator * denominator, 1e-12);
    float lambdaV = nl * sqrt(max(nv * nv * (1.0 - a2) + a2, 0.0));
    float lambdaL = nv * sqrt(max(nl * nl * (1.0 - a2) + a2, 0.0));
    float visibility = 0.5 / max(lambdaV + lambdaL, 1e-8);
    return materialFresnel(f0, vh) * distribution * visibility * nl;
}
''' + s[b:]
# Validate exact per-pixel material identity through matching opaque view depth, no colour inference.
helper='''
bool rawAlbedoMatchesSurface(vec4 raw, vec3 viewPosition, float packedAvailable)
{
    if (packedAvailable < 0.5 || raw.a >= -0.0001 || viewPosition.z >= -0.0001
        || any(isnan(raw)) || any(isinf(raw)) || any(lessThan(raw.rgb, vec3(0.0)))) return false;
    // Both native position and optional albedo use floating attachments. Account for half-float
    // depth quantization without accepting a nearby foreground object as the old receiver.
    float tolerance = max(0.002, abs(viewPosition.z) * 0.001);
    return abs(raw.a - viewPosition.z) <= tolerance;
}

vec3 resolveSurfaceBaseColor(vec3 source, vec4 material, float authoredLuminance,
    float dynamicSurface, vec4 raw, vec3 position, float packedAvailable)
{
    if (rawAlbedoEnabled != 0 && rawAlbedoMatchesSurface(raw, position, packedAvailable))
        return clamp(raw.rgb, vec3(0.0), vec3(1.0));
    vec3 sourceLinear = srgbToLinear(source);
    if (dynamicSurface > 0.5)
        return authoredLuminance >= 0.0
            ? clamp(sourceLinear * authoredLuminance / max(dot(sourceLinear, LUMA), 0.02), 0.0, 1.0)
            : sourceLinear;
    return material.a >= 0.05 ? reconstructSurfaceAlbedo(source, material, authoredLuminance) : sourceLinear;
}
'''
a=s.index('vec3 reconstructSurfaceEmission(');s=s[:a]+helper+'\n'+s[a:]
# The actual material is known before evaluating per-light Fresnel.
s=once(s,'''    float authoredMetallicHint,
    vec4 filteredPointVisibilityA,''', '''    float authoredMetallicHint,
    vec3 materialF0,
    vec4 filteredPointVisibilityA,''')
s=once(s,'''                    lightDirection,
                    surfaceRoughness);''', '''                    lightDirection,
                    surfaceRoughness,
                    materialF0) * emissiveLightStrength;''')
s=once(s,'''                    sunDirection,
                    surfaceRoughness);''', '''                    sunDirection,
                    surfaceRoughness,
                    materialF0) * sunLightStrength;''')
# Use actual surface albedo for direct tint and final material composition, while compatibility
# remains explicitly bounded for unsupported shaders rather than pretending they wrote raw RGB.
s=once(s,'''    vec3 worldPosition = earlyWorldPosition;''', '''    vec4 rawSurfaceAlbedo = rawAlbedoEnabled != 0
        ? readGBufferTexel(gUnlitAlbedo, uv) : vec4(0.0);
    vec3 worldPosition = earlyWorldPosition;''')
s=once(s,'''                voxelLighting = traceVoxelPointLight(''', '''                vec4 directMaterial = dynamicSurface > 0.5
                    ? vec4(0.0) : sampleVoxelAtWorld(worldPosition - worldGeometricNormal * 0.08);
                float directMetallic = mix((1.0 - dynamicSurface)
                    * smoothstep(0.66, 0.74, directMaterial.a), authoredMetallic, materialMapPresent);
                vec3 directAlbedo = resolveSurfaceBaseColor(source, directMaterial, authoredAlbedoLuminance,
                    dynamicSurface, rawSurfaceAlbedo, position, packedSurfaceAvailable);
                vec3 materialF0 = mix(vec3(0.04), directAlbedo, directMetallic);
                voxelLighting = traceVoxelPointLight(''')
s=once(s,'''                    materialMapPresent * authoredMetallic,
                    resolvedPointVisibilityA,''', '''                    directMetallic,
                    materialF0,
                    resolvedPointVisibilityA,''')
a=s.index('    float sourceLinearLuminance = dot(');b=s.index('\n    // Alpha is a compact material class',a)
s=s[:a]+'''    vec3 surfaceAlbedo = resolveSurfaceBaseColor(source, voxelLighting.material,
        authoredAlbedoLuminance, dynamicSurface, rawSurfaceAlbedo, position, packedSurfaceAvailable);
''' + s[b:]
# Remove material-specific energy compensation. These paths now evaluate the actual F0.
a=s.index('    vec3 materialSpecularTint = mix(');b=s.index('    // Water, ice and glass already contain transmission/refraction',a)
s=s[:a]+'''    // Keep authored roughness and material Fresnel; a metal is not automatically polished.
    float resolvedPbrRoughness = filterSpecularRoughness(surfaceRoughness, worldNormal);
    float resolvedSpecularEligibility = 1.0;
    vec3 worldViewDirection = normalize(cameraWorldPosition - worldPosition);
    vec3 directSpecularRadiance = voxelLighting.directSpecular;
''' + s[b:]
s=once(s,'''    float diffuseTransportEligibility = (1.0 - metallic * 0.88)''', '''    float diffuseTransportEligibility = (1.0 - metallic)''')
s=once(s,'''    vec3 fresnelReflectance = baseReflectance
        + (vec3(1.0) - baseReflectance) * pow(1.0 - normalView, 5.0);''', '''    vec3 fresnelReflectance = materialFresnel(baseReflectance, normalView);''')
s=once(s,'''        * reflectionVisibility
        * (1.15 + metallic * 1.25 + planarResponse * 0.85);''', '''        * reflectionVisibility;''')
# A diffuse irradiance lobe is not an independently observed specular light. Do not count it twice.
a=s.index('    vec3 localEnvironmentSpecular = vec3(0.0);');b=s.index('\n    // The normal map shapes BRDF response',a)
s=s[:a]+'''    // Unresolved specular directions remain unresolved; diffuse cache is not a fake spotlight.
''' + s[b:]
s=once(s,'''    float reflectionAddWeight = 0.82
        + metallic * 3.00
        + max(planarResponse, transmissiveSurface) * 0.18;''', '''    float reflectionAddWeight = 1.0;''')
s=once(s,'        + directSpecularRadiance * 1.15','        + directSpecularRadiance')
s=once(s,'        + directSpecularRadiance * 1.28','        + directSpecularRadiance')
# Reconstruct secondary radiance in linear space too. Preserve ReflectionResult's encoded carrier
# ABI by encoding only once on return, never multiply two display-domain colours together.
a,o,b=span(s,'vec3 shadeVoxelReflectionHit(');method=s[a:b]
method=once(method,'''    vec3 reflectedColor = hitAlbedo * 0.115;''', '''    vec3 linearAlbedo = srgbToLinear(clamp(hitAlbedo, 0.0, 1.0));
    vec3 reflectedColor = linearAlbedo * sampleVoxelIrradiance(hitPosition, hitNormal)
        * pointLightBounceStrength;''')
method=once(method,'''    reflectedColor += hitAlbedo
        * sunColorStrength.rgb''', '''    reflectedColor += linearAlbedo
        * srgbToLinear(sunColorStrength.rgb)''')
method=once(method,'''        * reflectedSunVisibility
        * 0.42;''', '''        * reflectedSunVisibility * sunLightStrength;''')
method=once(method,'lightIndex >= voxelLightCount || lightIndex >= 2','lightIndex >= voxelLightCount')
method=once(method,'''        reflectedColor += hitAlbedo
            * lightColorRadius.rgb''', '''        reflectedColor += linearAlbedo
            * emitterColor(lightColorRadius.rgb)''')
method=once(method,'''            * incidentRadiance
            * 0.75;''', '''            * incidentRadiance * emissiveLightStrength;''')
method=once(method,'''    return clamp(reflectedColor, 0.0, 1.4);''', '''    return linearToSrgb(max(reflectedColor, vec3(0.0)));''')
s=s[:a]+method+s[b:]
# A deterministic skewed mirror is not a rough microfacet integral. Preserve the correct central
# direction while the proper multi-direction BRDF sampling remains a separate planned backend.
a=s.index('    // One coherent cone sample widens rough reflections.');b=s.index('    vec3 rayOrigin = worldPosition',a)
s=s[:a]+'''    // This compatibility query follows the actual mirror direction. Roughness changes
    // confidence/range, not a fixed sideways offset of the reflected scene.
''' + s[b:]
sources[p]=s
for path,source in sources.items():
    (ROOT/path).write_text(source,encoding='utf-8',newline='\n')
    print('UPDATED',path,blob(source.encode()))
