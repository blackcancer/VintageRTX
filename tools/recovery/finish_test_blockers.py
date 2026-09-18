"""Complete the documented contract migration after apply_test_blockers.py.
No renderer threshold or image gate is relaxed here. Obsolete source-spelling checks are replaced
by the exact-reader/no-dilation contract already exercised by the production GLSL tests.
"""
from pathlib import Path
import hashlib
ROOT = Path(__file__).resolve().parents[2]

def load(path, expected=None):
    data = (ROOT / path).read_bytes()
    if expected:
        actual = hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
        if actual != expected: raise ValueError('Unexpected source revision: '+path)
    return data.decode('utf-8').replace('\r\n','\n')

def once(s, old, new):
    if s.count(old) != 1: raise ValueError(f'Expected one anchor ({s.count(old)}): '+old[:100])
    return s.replace(old,new,1)

changes = {}
p = 'tests/VintageRTX.Test/PreflightSuite.cs'
s = load(p, '3dfd8527f3c3c78c3b03dcd78398d4672625da7c')
s = once(s, '''        Assert(shader.Contains("consistentNeighbours >= 2", StringComparison.Ordinal)
            && shader.Contains("conservative G-buffer dilation", StringComparison.Ordinal),
            "one-pixel opaque G-buffer seam repair missing");''', '''        Assert(!shader.Contains("consistentNeighbours >= 2", StringComparison.Ordinal)
            && shader.Contains("vec4 readGBufferTexel(", StringComparison.Ordinal)
            && shader.Contains("resolveSurfaceShadow(position, normal,", StringComparison.Ordinal),
            "geometry-exact silhouette and shadow reconstruction contract missing");''')
s = once(s, '''        Assert(shader.Contains("cameraAlignedLight", StringComparison.Ordinal)
            && shader.Contains("floatingWorldOrigin) < 0.75", StringComparison.Ordinal),
            "camera-aligned held-light self-intersection guard missing");''', '''        Assert(!shader.Contains("floatingWorldOrigin) < 0.75", StringComparison.Ordinal)
            && shader.Contains("visibility += traceVoxelVisibility", StringComparison.Ordinal),
            "held emitters must trace world occlusion, not receive unconditional visibility");''')
s = once(s, '''            && displayShader.Contains("if (!hasGeometry && deferredGeometryRequired && shadowPass == 0)", StringComparison.Ordinal),''', '''            && displayShader.Contains("position = readGBufferTexel(gPosition, uv).xyz", StringComparison.Ordinal)
            && displayShader.Contains("encodedNormalRoughness = readGBufferTexel(gNormal, uv)", StringComparison.Ordinal),''')
changes[p] = s

p = 'tests/VintageRTX.Test/TestPaths.cs'
s = load(p, 'c913ea0b268b46f551c3b2958dfce6934768e4ca')
s = once(s, '''        if (!File.Exists(Path.Combine(root, "Vintagestory.exe")))
        {
            throw new FileNotFoundException("Vintagestory.exe was not found.", Path.Combine(root, "Vintagestory.exe"));
        }''', '''        // Asset/bootstrap tests also run against the official Linux client. Validating
        // a client installation is distinct from selecting an executable to launch in-game.
        if (!File.Exists(Path.Combine(root, "VintagestoryAPI.dll"))
            || !Directory.Exists(Path.Combine(root, "assets", "game")))
        {
            throw new DirectoryNotFoundException($"A complete Vintage Story client (API and game assets) was not found at '{root}'.");
        }''')
changes[p] = s

p = 'tests/VintageRTX.Test/RuntimeCoverageProbeWorldTests.cs'
s = load(p)
old = '''        harness.RainHeightAt = static (x, z) => Math.Abs(x) <= 1 && Math.Abs(z) <= 1 ? 82 : 78;
        harness.FallbackBlockAt = (x, y, z, layer) =>
            layer != BlockLayersAccess.Fluid
                && (y == harness.RainHeightAt(x, z)
                    || (Math.Abs(x) <= 1 && Math.Abs(z) <= 1 && y == 80))
                ? harness.Solid
                : harness.Air;'''
new = '''        // The ground must not be another slanted roofing block: the production solar
        // witness intentionally walks past roof cells until it reaches real exposed ground.
        Block ground = new()
        {
            Id = 2,
            Code = new AssetLocation("game:rock-granite"),
            BlockMaterial = EnumBlockMaterial.Stone,
            LightAbsorption = 32
        };
        harness.RainHeightAt = static (x, z) => Math.Abs(x) <= 1 && Math.Abs(z) <= 1 ? 82 : 78;
        harness.FallbackBlockAt = (x, y, z, layer) =>
            layer == BlockLayersAccess.Fluid ? harness.Air
                : Math.Abs(x) <= 1 && Math.Abs(z) <= 1 && (y == 82 || y == 80) ? harness.Solid
                : y == 78 ? ground : harness.Air;'''
s = once(s, old, new)
changes[p] = s
for path, source in changes.items():
    (ROOT / path).write_text(source, encoding='utf-8', newline='\n')
    print('UPDATED',path)
