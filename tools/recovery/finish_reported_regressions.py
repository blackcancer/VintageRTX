"""Complete the three residual contracts revealed by the first red/green run."""
from pathlib import Path
import runpy

ROOT = Path(__file__).resolve().parents[2]
state = runpy.run_path(str(ROOT/'tools/recovery/repair_reported_regressions.py'))
once, body, load = (state[name] for name in ('once', 'body', 'load'))

p = 'tests/VintageRTX.Test/FilmicDisplayRendererLogicCoverageTests.cs'
s = load(p, 'f129029e99cf19fea4107fb94ef431588b138e87')
a,b,method = body(s, 'public void MatricesDynamicLightsSunAndDisabledVoxelBindingAreDeterministic()')
method = once(method, 'Assert.AreEqual(4, GetField<int>(renderer, "availableDynamicLightCount"));',
'''Assert.AreEqual(2, GetField<int>(renderer, "availableDynamicLightCount"),
                "The zero-energy and non-finite entries are not available emitter candidates.");''')
(ROOT/p).write_text(s[:a]+method+s[b:], encoding='utf-8', newline='\n')

p = 'tests/VintageRTX.Test/LiquidShaderPhysicsTests.cs'
s = (ROOT/p).read_text(encoding='utf-8')
s = once(s, '''        Assert.IsFalse(shader.Contains("vec3 recoveredShoreSource = mix(", StringComparison.Ordinal));''',
r'''        // A legacy body is still present but constant-folded away. Require its
        // gate to stay zero and forbid a later assignment that could revive it.
        StringAssert.Contains(shader, "float partialLiquidRecovery = 0.0;");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(
            shader, @"\bpartialLiquidRecovery\s*=").Count);''')
(ROOT/p).write_text(s, encoding='utf-8', newline='\n')

p = 'tests/VintageRTX.Test/VoxelSceneCoverageDeepTests.cs'
s = (ROOT/p).read_text(encoding='utf-8')
a,b,method = body(s, 'public void BlockChangeLifecycleSeparatesOutsideDirtyAndEmissiveRebuildPaths()')
method = once(method, 'using SceneFixture fixture = new(meshes:', 'using SceneFixture fixture = new(withPlayer: true, meshes:')
method = once(method, '        fixture.SetOrigins(0, 0, 0, 0, 0, 0);', '''        fixture.SetOrigins(0, 0, 0, 0, 0, 0);
        fixture.SetField("generation", 1);
        fixture.SetField("rebuildRequested", false);
        int dimension = fixture.Field<ICoreClientAPI>("api").World.Player.Entity.Pos.Dimension;
        fixture.Invoke<object>("OnBlockChanged", new BlockPos(2, 2, 2, dimension + 1), old);
        Assert.AreEqual(0, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);''')
method = method.replace('new BlockPos(2, 2, 2)', 'new BlockPos(2, 2, 2, dimension)')
method = once(method, '''        Assert.IsTrue(fixture.Field<bool>("rebuildRequested"));
        Assert.AreEqual(0, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);''', '''        Assert.IsFalse(fixture.Field<bool>("rebuildRequested"),
            "Changing an emitter must not force a full terrain-volume rebuild.");
        Assert.AreEqual(1, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);
        fixture.Invoke<object>("ProcessDirtyBlocks");
        Assert.AreEqual(0, fixture.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);
        Assert.AreEqual(1, fixture.Field<VoxelLight[]>("readyLights").Length);
        Assert.AreEqual("game:torch", fixture.Field<VoxelLight[]>("readyLights")[0].Code);
        Assert.IsTrue(fixture.Field<bool>("liveLightsUploadPending"));
        Assert.IsTrue(fixture.Field<bool>("radianceRefreshRequested"));
        using SceneFixture noPlayer = new();
        noPlayer.SetOrigins(0, 0, 0, 0, 0, 0);
        noPlayer.Invoke<object>("OnBlockChanged", new BlockPos(2, 2, 2), old);
        Assert.AreEqual(0, noPlayer.Field<ConcurrentDictionary<(int, int, int), byte>>("dirtyBlocks").Count);''')
method = method.replace('BlockChangeLifecycleSeparatesOutsideDirtyAndEmissiveRebuildPaths',
    'BlockChangeLifecycleCoalescesSameDimensionEditsAndRefreshesEmitters')
(ROOT/p).write_text(s[:a]+method+s[b:], encoding='utf-8', newline='\n')
print('Completed available-candidate, inactive legacy shoreline and same-dimension live-emitter contracts.')
