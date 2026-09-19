"""Update secondary-transport wiring contracts, keeping numerical and visual gates separate.
Run after the production shader patch; validates exact prior preflight bytes. The executable GLSL
suite independently rejects black-surface emission, truncated light sets and shared sky/sun masks.
"""
from pathlib import Path
import hashlib
ROOT=Path(__file__).resolve().parents[2]
p=ROOT/'tests/VintageRTX.Test/PreflightSuite.cs'
data=p.read_bytes()
actual=hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
if actual!='ff068f5433f875c7b3a8016cc0e121fa77364435':
    raise ValueError('Preflight changed concurrently; review rather than overwrite it.')
s=data.decode('utf-8')
pairs=[(
'''        Assert(shader.Contains("Performance mode retains a genuine indirect sun path", StringComparison.Ordinal)
            && shader.Contains("bounceSunDistance", StringComparison.Ordinal),
            "performance-tier secondary sun bounce missing");''',
'''        // The same solar visibility path is evaluated independently of the sky-ray budget.
        // tests/transport also executes blocked-sun/visible-sky and converse fixtures.
        Assert(shader.Contains("float bouncedSunVisibility = traceSunVisibility(", StringComparison.Ordinal)
            && shader.Contains("bouncedSunReceiver * bouncedSunVisibility * sunLightStrength", StringComparison.Ordinal)
            && !shader.Contains("bouncedSunVisibility = bouncedSkyVisibility", StringComparison.Ordinal),
            "secondary solar visibility must be traced independently rather than borrowed from a sky sample");'''),(
'''        Assert(shader.Contains("diffuseReflectance", StringComparison.Ordinal), "voxel-bounce diffuse reflectance floor missing");''',
'''        Assert(shader.Contains("accumulated += linearHitAlbedo * incident * distanceFade", StringComparison.Ordinal)
            && shader.Contains("maximumComponent(linearHitAlbedo) <= 0.0", StringComparison.Ordinal),
            "secondary transport must preserve material RGB without inventing a neutral reflectance floor");''')]
for old,new in pairs:
    if s.count(old)!=1: raise ValueError('Expected exactly one prior contract: '+old[:90])
    s=s.replace(old,new,1)
p.write_text(s,encoding='utf-8',newline='\n')
print('Preflight now requires independent solar visibility and real RGB reflectance; no image thresholds changed.')
