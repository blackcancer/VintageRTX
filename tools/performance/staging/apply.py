"""One-shot exact-base optimization. Never imported by MSBuild or the mod."""
from pathlib import Path
import hashlib
import shutil
ROOT = Path(__file__).resolve().parents[3]
P = ROOT / 'src/VintageRTX/Rendering/LiquidSurfaceSimulation.cs'
G = ROOT / 'src/VintageRTX/assets/vintagertx/shaders/display.frag'
def blob(data):
    return hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
assert blob(P.read_bytes()) == 'ad2b4728ef4272d73024c53eb32b116e1d7446d9'
assert blob(G.read_bytes()) == '9398ede01bc98aef3d3f6b2ed7b2bcc6322037f3'
s=P.read_text(encoding='utf-8')
def once(old,new):
    global s
    if s.count(old)!=1: raise ValueError(('ambiguous anchor',old[:90],s.count(old)))
    s=s.replace(old,new,1)
def span(signature):
    a=s.index(signature); o=s.index('{',a); b=o+1; d=1
    while d:
        d+=(s[b]=='{')-(s[b]=='}'); b+=1
    return a,b
def replace_func(signature,new):
    global s
    a,b=span(signature);s=s[:a]+new+s[b:]
once('internal sealed class LiquidSurfaceSimulation','internal sealed partial class LiquidSurfaceSimulation')
once('        surfacePhysics.Validate();','        surfacePhysics.Validate();\n        topologyDirty = true;')
once('''    public void ClearSurfaceCell(int x, int z)
    {
        int index = GetIndexChecked(x, z);''','''    public void ClearSurfaceCell(int x, int z)
    {
        int index = GetIndexChecked(x, z);
        topologyDirty = true;''')
once('''    private void Step(in LiquidSurfaceForcing forcing)
    {
        ApplyPendingImpulses();''','''    private void Step(in LiquidSurfaceForcing forcing)
    {
        EnsureActiveTopology();
        ApplyPendingImpulses();''')
a=s.index('        float inverseCellSizeSquared =',s.index('    private void Step('))
b=s.index('        // Every destination cell',a)
loop=s[a:b];start=loop.index('                ref readonly CellStepCoefficients');end=loop.index('            }\n        }',start)
body=loop[start:end]
for old,new in [('ConnectedHeight(x - 1, z, index, center)','heights[stencil.Left]'),('ConnectedHeight(x + 1, z, index, center)','heights[stencil.Right]'),('ConnectedHeight(x, z - 1, index, center)','heights[stencil.Up]'),('ConnectedHeight(x, z + 1, index, center)','heights[stencil.Down]')]:
    assert body.count(old)==1;body=body.replace(old,new)
body='\n'.join(l[4:] if l.startswith('    ') else l for l in body.split('\n'))
s=s[:a]+'''        float inverseCellSizeSquared = 1.0f / (CellSize * CellSize);
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
            ref readonly CellStencil stencil = ref activeStencils[ordinal];
            int index = stencil.Center;
'''+body+'''        }

'''+s[b:]
once('''        // Every destination cell is assigned above, including inactive cells.
        // Promote the completed state by swapping ownership instead of copying
        // both full grids after every fixed step. The former current buffers
        // become scratch storage and are completely overwritten on the next step.''','''        // All active destinations are written. ClearSurfaceCell zeros BOTH banks when a cell
        // is removed, so inactive cells remain zero without revisiting the entire dry grid.
        // Preserve the existing ping-pong ownership and fixed-step update order.''')
replace_func('    private void ComputeNormals()', '''    private void ComputeNormals()
    {
        EnsureActiveTopology();
        float inverseDoubleCellSize = 0.5f / CellSize;
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
            ref readonly CellStencil stencil = ref activeStencils[ordinal];
            int index = stencil.Center;
            float slopeX = ((heights[stencil.Right] + bubbleBulge[stencil.Right])
                - (heights[stencil.Left] + bubbleBulge[stencil.Left])) * inverseDoubleCellSize;
            float slopeZ = ((heights[stencil.Down] + bubbleBulge[stencil.Down])
                - (heights[stencil.Up] + bubbleBulge[stencil.Up])) * inverseDoubleCellSize;
            float inverseLength = 1.0f / MathF.Sqrt(1.0f + slopeX * slopeX + slopeZ * slopeZ);
            normalX[index] = -slopeX * inverseLength;
            normalZ[index] = -slopeZ * inverseLength;
        }
    }''')
for signature in ['    private void ApplyWind(', '    private void RefreshWindForcingCache(']:
    a,b=span(signature); fun=s[a:b]
    old='''        for (int z = 0; z < Depth; z++)
        {
            for (int x = 0; x < Width; x++)
            {
                int index = GetIndex(x, z);'''
    assert fun.count(old)==1
    new='''        EnsureActiveTopology();
        for (int ordinal = 0; ordinal < activeStencilCount; ordinal++)
        {
                int index = activeStencils[ordinal].Center;'''
    if 'RefreshWind' in signature: new+='\n                int x = index % Width;\n                int z = index / Width;'
    fun=fun.replace(old,new,1)
    assert fun.count('            }\n        }')==1
    fun=fun.replace('            }\n        }','        }',1)
    if 'ApplyWind' in signature:
        fun=fun.replace('''                if (!IsActive(index))
                {
                    continue;
                }

''','')
    else: fun=fun.replace('!IsActive(index) || cached.AngularFrequencySquared','cached.AngularFrequencySquared')
    begin=fun.index('        for (int ordinal'); end=fun.index('\n        }',begin)
    lines=fun[begin:end].splitlines(True)
    fun=fun[:begin]+''.join(l[4:] if i>1 and l.startswith('    ') else l for i,l in enumerate(lines))+fun[end:]
    s=s[:a]+fun+s[b:]
a=s.index('        int exposedCellCount = 0;',s.index('    private void ApplyRain('));b=s.index('        if (exposedCellCount == 0)',a)
s=s[:a]+'''        EnsureActiveTopology();
        int exposedCellCount = rainExposedCellCount;
'''+s[b:]
a=s.index('            for (int probe = 0;',s.index('    private void ApplyRain('));b=s.index('                int x = index % Width;',a)
s=s[:a]+'            int index = rainSuccessors[start];\n'+s[b:]
a=s.index('            int index = rainSuccessors[start];');b=s.index('    /// <summary>\n    /// Drives a narrow',a)
sub=s[a:b];assert sub.count('                break;\n            }')==1
sub=sub.replace('                break;\n            }','',1)
end=sub.index('\n        }')
sub='\n'.join(l[4:] if i>0 and l.startswith('                ') else l for i,l in enumerate(sub[:end].split('\n'))).rstrip()+'\n'+sub[end:]
s=s[:a]+sub+s[b:]
a=s.index('        Array.Clear(bubbleAreaByProfile);',s.index('    private void AdvanceBubbles('));b=s.index('        int spawnedThisStep =',a)
s=s[:a]+'        EnsureActiveTopology();\n\n'+s[b:]
replace_func('    private int FindProfileCell(','''    private int FindProfileCell(int profileId, int ordinal)
    {
        EnsureActiveTopology();
        if ((uint)profileId >= (uint)profileCellCounts.Length
            || (uint)ordinal >= (uint)profileCellCounts[profileId]) return 0;
        return profileCellIndices[profileCellOffsets[profileId] + ordinal];
    }''')
g=G.read_text(encoding='utf-8')
old='''        ivec2 corner = ivec2(i & 1, i >> 1);
        ivec2 pixel = clamp(basePixel + corner, ivec2(0), size - ivec2(1));'''
new='''        ivec2 corner = ivec2(i & 1, i >> 1);
        vec2 w = mix(vec2(1.0) - fraction, fraction, vec2(corner));
        // Exactly zero bilinear weight cannot contribute: skip its geometry/transport reads.
        // No epsilon pruning, receiver substitution, or change to edge fallback is permitted.
        if (w.x * w.y == 0.0) continue;
        ivec2 pixel = clamp(basePixel + corner, ivec2(0), size - ivec2(1));'''
assert g.count(old)==1;g=g.replace(old,new)
old='''        vec2 w = mix(vec2(1.0) - fraction, fraction, vec2(corner));
        float weight = w.x * w.y * exp(-planeError / tolerance) * smoothstep(0.6, 0.95, normalAgreement);'''
assert g.count(old)==1;g=g.replace(old,'        float weight = w.x * w.y * exp(-planeError / tolerance) * smoothstep(0.6, 0.95, normalAgreement);')
P.write_text(s,encoding='utf-8',newline='\n');G.write_text(g,encoding='utf-8',newline='\n')
STAGE=Path(__file__).resolve().parent
shutil.copyfile(STAGE/'LiquidSurfaceTopology.cs.txt',ROOT/'src/VintageRTX/Rendering/LiquidSurfaceTopology.cs')
shutil.copyfile(STAGE/'LiquidSurfaceTopologyTests.cs.txt',ROOT/'tests/VintageRTX.Test/LiquidSurfaceTopologyTests.cs')
print('Exact-base liquid and zero-weight reconstruction changes applied.')
