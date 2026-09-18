"""Apply the reviewed material migration and its new executable contracts once in CI.
The game and MSBuild never execute this file. No image acceptance threshold is changed.
"""
from pathlib import Path
import hashlib
ROOT=Path(__file__).resolve().parents[2]
def once(s,old,new):
    if s.count(old)!=1: raise ValueError(f'Expected one anchor ({s.count(old)}): '+old[:160])
    return s.replace(old,new,1)
def edit(path,old,new):
    p=ROOT/path;s=p.read_text(encoding='utf-8')
    p.write_text(once(s,old,new),encoding='utf-8',newline='\n')
recipe=ROOT/'tools/recovery/apply_material_transport.py'
data=recipe.read_bytes()
assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()=='6a5d4042cc2619416b38c8b7edc216ccba7d15eb'
code=data.decode()
code=once(code,'        GL.ActiveTexture(TextureUnit.Texture28);\n        GL.GetInteger(GetPName.TextureBinding2D, out int previousRawAlbedoBinding);',
'''        GL.GetInteger(GetPName.ActiveTexture, out int previousRawAlbedoUnit);
        GL.ActiveTexture(TextureUnit.Texture28);
        GL.GetInteger(GetPName.TextureBinding2D, out int previousRawAlbedoBinding);''')
code=once(code,'GL.ActiveTexture((TextureUnit)state.ActiveTexture);','GL.ActiveTexture((TextureUnit)previousRawAlbedoUnit);')
code=once(code,'''        api.Event.UnregisterRenderer(rawAlbedoCapture, EnumRenderStage.Opaque);
        rawAlbedoCapture.Dispose();''', '''        if (rawAlbedoCapture is not null)
        {
            api.Event.UnregisterRenderer(rawAlbedoCapture, EnumRenderStage.Opaque);
            rawAlbedoCapture.Dispose();
        }''')
# Shader creation also occurs during initialization. Only explicit reload invalidates an
# active opaque transaction: scope the replacement to that method, not both occurrences.
old="s=once(s,'''            shader = CreateShader();''', '''            rawAlbedoCapture.ReloadShader();\n            shader = CreateShader();''')"
new="a,o,b=span(s,'    public bool ReloadShader()')\nmethod=once(s[a:b], '            shader = CreateShader();', '            rawAlbedoCapture.ReloadShader();\\n            shader = CreateShader();')\ns=s[:a]+method+s[b:]"
code=once(code,old,new)
exec(compile(code,str(recipe),'exec'),{'__file__':str(recipe),'__name__':'__main__'})
p='src/VintageRTX/assets/vintagertx/shaders/display.frag'
edit(p,'''    float voxelMetallicHint = smoothstep(0.66, 0.74, result.material.a);
    float directSpecularHint = max(
        max(authoredMetallicHint, voxelMetallicHint),
        1.0 - smoothstep(0.38, 0.72, surfaceRoughness));''', '''    // A rough dielectric still reflects light. Evaluate its real material lobe instead
    // of switching normal-map response off at an arbitrary roughness threshold.
    float directSpecularHint = 1.0;''')
edit(p,'''    vec4 rawSurfaceAlbedo = rawAlbedoEnabled != 0
        ? readGBufferTexel(gUnlitAlbedo, uv) : vec4(0.0);''', '''    vec4 rawSurfaceAlbedo = rawAlbedoEnabled != 0
            && all(equal(textureSize(gUnlitAlbedo, 0), textureSize(gPosition, 0)))
        ? readGBufferTexel(gUnlitAlbedo, uv) : vec4(0.0);''')
edit('src/VintageRTX/Rendering/RawAlbedoCapture.cs',
'''        if (disposed || stage != EnumRenderStage.Opaque) return;
        if (!enabled || faulted)''',
'''        if (disposed || stage != EnumRenderStage.Opaque) return;
        target.End(false);
        if (!enabled || faulted)''')
edit('src/VintageRTX/Rendering/ReflectionSourceCaptureRenderer.cs',
'''    public ReflectionSourceCaptureRenderer(ICoreClientAPI api, RawAlbedoCapture? rawAlbedo = null)''',
'''    /// <param name="rawAlbedo">Optional owned material transaction ending before the snapshot.</param>
    public ReflectionSourceCaptureRenderer(ICoreClientAPI api, RawAlbedoCapture? rawAlbedo = null)''')
p='tests/VintageRTX.Test/PreflightSuite.cs'
edit(p,'''        Assert(shader.Contains("vec3 reflectedColor = hitAlbedo * 0.115", StringComparison.Ordinal),
            "off-screen reflection hits need bounded local indirect radiance");''', '''        Assert(shader.Contains("vec3 linearAlbedo = srgbToLinear", StringComparison.Ordinal)
            && shader.Contains("linearAlbedo * sampleVoxelIrradiance(hitPosition, hitNormal)", StringComparison.Ordinal)
            && shader.Contains("return linearToSrgb(max(reflectedColor, vec3(0.0)))", StringComparison.Ordinal),
            "secondary lighting must accumulate linear radiance before encoding its compatibility carrier");''')
edit(p,'''        Assert(shader.Contains("float phase = 0.0", StringComparison.Ordinal)
            && !shader.Contains("float(temporalFrameIndex & 7) * 2.39996322973", StringComparison.Ordinal),
            "voxel reflections must not rotate a sparse cone sample between frames");''', '''        Assert(!shader.Contains("float coneWidth = roughness * roughness * 0.26", StringComparison.Ordinal)
            && shader.Contains("normalize(reflect(incidentDirection, worldNormal))", StringComparison.Ordinal)
            && !shader.Contains("float(temporalFrameIndex & 7) * 2.39996322973", StringComparison.Ordinal),
            "the compatibility mirror query must not shift scenery using a fixed or animated cone offset");''')
edit(p,'''            && shader.Contains("irradianceSpecularRadiance", StringComparison.Ordinal)''', '''            && !shader.Contains("irradianceSpecularRadiance", StringComparison.Ordinal)
            && shader.Contains("vec3 directSpecularRadiance = voxelLighting.directSpecular", StringComparison.Ordinal)''')
print('Material migration completed; numerical GLSL/MRT tests are mandatory before publication.')
