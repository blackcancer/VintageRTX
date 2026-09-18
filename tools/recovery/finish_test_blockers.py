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
