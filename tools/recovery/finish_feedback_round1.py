"""Final exact transformations before round-one compilation and source publication."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
def edit(path, old, new):
    p = ROOT / path
    s = p.read_text(encoding='utf-8')
    if s.count(old) != 1:
        raise ValueError(f'Expected exactly one final anchor in {path}: {old[:100]}')
    p.write_text(s.replace(old, new), encoding='utf-8', newline='\n')
edit('src/VintageRTX/assets/vintagertx/shaders/display.frag',
    '    float surfaceTemporalBlend = 0.0; // No same-UV RGB history across moving objects/reflections.',
    '''    // Runtime currently supplies zero until motion/previous-geometry reprojection is available.
    // Keep the shader ABI live so uniform bindings and standalone diagnostic contexts stay valid.
    float surfaceTemporalBlend = temporalBlend
        * (1.0 - dynamicSurface)
        * (1.0 - smoothstep(0.001, 0.02, planarResponse));''')
edit('src/VintageRTX/Rendering/EmitterAppearance.cs',
    '    private static bool IsWarmSource(string code) => new[] { "lantern", "torch", "candle", "fire", "flame", "ember", "forge", "bloomery" }\n        .Any(part => code.Contains(part, StringComparison.OrdinalIgnoreCase));',
    '''    private static readonly string[] WarmFamilies =
        ["lantern", "torch", "candle", "fire", "flame", "ember", "forge", "bloomery"];

    /// <summary>Tests reusable family names without a per-source string-array allocation.</summary>
    private static bool IsWarmSource(string code) =>
        WarmFamilies.Any(part => code.Contains(part, StringComparison.OrdinalIgnoreCase));''')
edit('src/VintageRTX/Rendering/VoxelScene.cs',
    '    private void ProcessDirtyBlocks()',
    '''    /// <summary>Consumes at most sixteen queued edits and invalidates their dependent light data.</summary>
    private void ProcessDirtyBlocks()''')
edit('src/VintageRTX/Rendering/FilmicDisplayRenderer.cs',
    '    private void BindVoxelUniforms(',
    '''    /// <summary>Binds one coherent scene and light selection after incremental light uploads.</summary>
    /// <param name="config">Current normalized rendering options.</param>
    /// <param name="maximumVoxelLights">Maximum simultaneous light slots.</param>
    private void BindVoxelUniforms(''')
# The old block belongs to BindVoxelUniforms, not the inserted upload helper.
p = ROOT / 'src/VintageRTX/Rendering/FilmicDisplayRenderer.cs'
s = p.read_text(encoding='utf-8')
marker = '    /// <summary>Updates small light/caster tables independently from full geometry publication.</summary>'
i = s.index(marker)
prefix = s[:i]
start = prefix.rfind('    /// <summary>')
if start < 0: raise ValueError('Missing previous XML block')
if any(line.strip() and not line.lstrip().startswith('///') for line in prefix[start:].splitlines()):
    raise ValueError('Unexpected code between XML blocks')
s = prefix[:start] + s[i:]
p.write_text(s, encoding='utf-8', newline='\n')
