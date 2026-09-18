"""Final exact transformations before round-one compilation and source publication."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
p = ROOT / 'src/VintageRTX/assets/vintagertx/shaders/display.frag'
s = p.read_text(encoding='utf-8')
old = '    float surfaceTemporalBlend = 0.0; // No same-UV RGB history across moving objects/reflections.'
new = '''    // Runtime currently supplies zero until motion/previous-geometry reprojection is available.
    // Keep the shader ABI live so uniform bindings and standalone diagnostic contexts stay valid.
    float surfaceTemporalBlend = temporalBlend
        * (1.0 - dynamicSurface)
        * (1.0 - smoothstep(0.001, 0.02, planarResponse));'''
assert s.count(old) == 1
p.write_text(s.replace(old, new), encoding='utf-8', newline='\n')
p = ROOT / 'src/VintageRTX/Rendering/EmitterAppearance.cs'
s = p.read_text(encoding='utf-8')
old = '    private static bool IsWarmSource(string code) => new[] { "lantern", "torch", "candle", "fire", "flame", "ember", "forge", "bloomery" }\n        .Any(part => code.Contains(part, StringComparison.OrdinalIgnoreCase));'
new = '''    private static readonly string[] WarmFamilies =
        ["lantern", "torch", "candle", "fire", "flame", "ember", "forge", "bloomery"];

    /// <summary>Tests reusable family names without a per-source string-array allocation.</summary>
    private static bool IsWarmSource(string code) =>
        WarmFamilies.Any(part => code.Contains(part, StringComparison.OrdinalIgnoreCase));'''
assert s.count(old) == 1
p.write_text(s.replace(old, new), encoding='utf-8', newline='\n')
