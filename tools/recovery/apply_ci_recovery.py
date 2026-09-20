"""Exact-base one-shot migration. Not a build hook; publish ordinary files only after verification."""
from pathlib import Path
import hashlib
import shutil

root=Path(__file__).resolve().parents[2]
staging=root/'tools/recovery/ci-staging'
base={
 'tests/VintageRTX.Test/PreflightSuite.cs':'9300263cc211bce129c3e5174b86c2a466ae2ccf',
 'tools/VintageRTX.RenderLab/StandaloneRenderer.cs':'c775fda51ad568858a9876fdba2d41a912af480d',
 'tools/VintageRTX.RenderLab/Program.cs':'1077efa3aa7be07496960796435abc6c09692672',
 'tools/VintageRTX.RenderLab/RenderLabExplorerTests.cs':'fe80844553c8e7102756914c04bafac1be73527a',
}
sources={}
for name,expected in base.items():
 b=(root/name).read_bytes()
 actual=hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()
 if actual!=expected: raise ValueError('Unexpected source revision: '+name+' '+actual)
 sources[name]=b.decode('utf-8')
def once(s,a,b):
 if s.count(a)!=1: raise ValueError('Ambiguous/missing anchor: '+a[:110])
 return s.replace(a,b,1)
p='tests/VintageRTX.Test/PreflightSuite.cs'
s=sources[p]
s=once(s,'''        Assert(shader.Contains("temporalFrameIndex % max(secondaryBounceCadence, 1) == 0", StringComparison.Ordinal)
            && shader.Contains("debugView == 8", StringComparison.Ordinal),
            "performance-tier temporal bounce cadence missing");''','''        // Diffuse transport is produced every frame on the reduced grid. Requiring the old
        // modulo cadence would reintroduce alternating radiance/zero into the actual game.
        Assert(!shader.Contains("temporalFrameIndex % max(secondaryBounceCadence, 1) == 0", StringComparison.Ordinal)
            && shader.Contains("filteredBounce", StringComparison.Ordinal)
            && shader.Contains("outShadowSun = vec4(rawSun, rawBounce)", StringComparison.Ordinal)
            && shader.Contains("outShadowSun = vec4(filteredSun, filteredBounce)", StringComparison.Ordinal),
            "current-frame HDR diffuse production/filter/resolve contract missing");
        for (int tier = 0; tier <= 2; tier++)
        {
            Assert(DiffuseTransportBudget.RayCount(tier, 1, 1f) == 1,
                "an enabled rendering tier must retain dynamic diffuse transport");
            Assert(DiffuseTransportBudget.RayCount(tier, 1, 0f) == 0,
                "explicitly disabled diffuse transport must remain disabled");
        }''')
s=once(s,r'''        Assert(
            renderer.Contains("shader.Uniform(\"secondaryBounceCadence\", 1)", StringComparison.Ordinal)
                && shader.Contains("secondaryBounceCadence <= 1", StringComparison.Ordinal)
                && shader.Contains("temporalFrameIndex % max(secondaryBounceCadence, 1) == 0", StringComparison.Ordinal),
            "every profile must trace coherent secondary radiance without zero-energy cadence frames");''',r'''        Assert(
            renderer.Contains("shader.Uniform(\"secondaryBounceCadence\", 1)", StringComparison.Ordinal)
                && shader.Contains("prefilteredShadowVisibility != 0 && secondaryBounceCadence > 0", StringComparison.Ordinal)
                && shader.Contains("? max(filteredBounce, vec3(0.0))", StringComparison.Ordinal)
                && !shader.Contains("temporalFrameIndex % max(secondaryBounceCadence, 1) == 0", StringComparison.Ordinal),
            "every profile must resolve current-frame secondary radiance without zero-energy cadence frames");''')
s=once(s,'''        Assert(
            renderer.Contains("directional irradiance field is the stable", StringComparison.Ordinal)
                && renderer.Contains("2 => 0", StringComparison.Ordinal),
            "performance tier must use the stable irradiance LOD without periodic bounce spikes");''','''        Assert(
            renderer.Contains("DiffuseTransportBudget.RayCount(", StringComparison.Ordinal)
                && renderer.Contains("effectiveVoxelBounceRayCount > 0", StringComparison.Ordinal)
                && renderer.Contains("PixelInternalFormat.Rgba16f", StringComparison.Ordinal)
                && shader.Contains("filteredBounce = accumulatedBounce / max(accumulatedWeight, 0.001)", StringComparison.Ordinal),
            "performance tier must keep current-frame dynamic HDR bounce instead of a static-only irradiance substitute");''')
sources[p]=s
p='tools/VintageRTX.RenderLab/StandaloneRenderer.cs'
s=sources[p]
s=once(s,'internal sealed class StandaloneRenderer : IDisposable','internal sealed partial class StandaloneRenderer : IDisposable')
s=once(s,'''        scene = new SyntheticScene(options.Width, options.Height);
        InitializeGlResources();''','''        Stopwatch phase = Stopwatch.StartNew();
        scene = new SyntheticScene(options.Width, options.Height);
        RecordPhase("synthetic-scene", phase);
        phase.Restart();
        try { InitializeGlResources(); }
        catch { Dispose(); throw; }
        RecordPhase("GL-initialization", phase);''')
s=once(s,'''        Dictionary<string, string> captures = new(StringComparer.OrdinalIgnoreCase);''','''        Stopwatch phase = Stopwatch.StartNew();
        Dictionary<string, string> captures = new(StringComparer.OrdinalIgnoreCase);''')
s=once(s,'''        // Diagnostic readback and PNG compression are outside the timing''','''        RecordPhase("diagnostic-draws-readback-analysis", phase);
        phase.Restart();
        // Diagnostic readback and PNG compression are outside the timing''')
s=once(s,'''        double[] gpuMilliseconds = MeasureGpuFrames(options.BenchmarkFrames);''','''        RecordPhase("warmup-30-frames", phase);
        phase.Restart();
        double[] gpuMilliseconds = MeasureGpuFrames(options.BenchmarkFrames);
        RecordPhase("benchmark-all-passes", phase);
        Console.WriteLine($"Transport draw count: {transportDrawCount} (three per frame, included in GPU queries).");''')
s=once(s,'''        CreateTexture2D(15, PixelInternalFormat.Rgba32f''','''        dynamicLiquidTexture = CreateTexture2D(15, PixelInternalFormat.Rgba32f''')
s=once(s,'''        outputTexture = GL.GenTexture();''','''        // Allocation uses the active unit. Preserve the just-uploaded liquid texture instead
        // of making the shader sample its own final render target on unit fifteen.
        int allocationBinding = GL.GetInteger(GetPName.TextureBinding2D);
        outputTexture = GL.GenTexture();''')
s=once(s,'''        GL.UseProgram(program);
        string[] samplerNames''','''        GL.BindTexture(TextureTarget.Texture2D, allocationBinding);
        GL.UseProgram(program);
        string[] samplerNames''')
s=once(s,'''        BindStaticUniforms();
        CheckGl("standalone initialization");''','''        BindStaticUniforms();
        InitializeTransportPipeline();
        ValidateTextureOwnership(outputTexture);
        CheckGl("standalone initialization");''')
s=once(s,'''        SetMatrix("inverseViewMatrix", inverseView);''','''        SetMatrix("inverseViewMatrix", inverseView);
        float[] view =
        [
            right.X, up.X, backward.X, 0,
            right.Y, up.Y, backward.Y, 0,
            right.Z, up.Z, backward.Z, 0,
            0, 0, 0, 1
        ];
        SetMatrix("viewMatrix", view);
        Matrix4x4 p = new(projection[0], projection[1], projection[2], projection[3],
            projection[4], projection[5], projection[6], projection[7],
            projection[8], projection[9], projection[10], projection[11],
            projection[12], projection[13], projection[14], projection[15]);
        if (!Matrix4x4.Invert(p, out Matrix4x4 inverse))
            throw new InvalidOperationException("Synthetic perspective projection is singular.");
        SetMatrix("inverseProjection", [inverse.M11, inverse.M12, inverse.M13, inverse.M14,
            inverse.M21, inverse.M22, inverse.M23, inverse.M24,
            inverse.M31, inverse.M32, inverse.M33, inverse.M34,
            inverse.M41, inverse.M42, inverse.M43, inverse.M44]);''')
s=once(s,'''        Set("voxelBounceSteps", 7);
        Set("voxelBounceShadowSteps", 6);''','''        Set("voxelBounceSteps", DiffuseTransportBudget.TraversalSteps(6f));
        Set("voxelBounceShadowSteps", DiffuseTransportBudget.TraversalSteps(6f));''')
a=s.index('    private void RenderFrame('); opening=s.index('{',a); i=opening+1; depth=1
while depth:
 depth+=(s[i]=='{')-(s[i]=='}'); i+=1
sig=s[a:opening].replace('private void','internal void')
s=s[:a]+sig+'{\n        RenderTransportFrame(view, frameIndex, outputColorDomain);\n    }'+s[i:]
s=once(s,'''        if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer);''','''        foreach (int target in transportFramebuffers)
            if (target != 0) GL.DeleteFramebuffer(target);
        if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer);''')
sources[p]=s
p='tools/VintageRTX.RenderLab/Program.cs'
s=sources[p]
s=once(s,'''        contextReady?.Invoke();''','''        System.Diagnostics.Stopwatch phase = System.Diagnostics.Stopwatch.StartNew();
        contextReady?.Invoke();
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "gpu-services-milliseconds.txt"),
            phase.Elapsed.TotalMilliseconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine($"RenderLab phase production-GPU-services: {phase.Elapsed.TotalMilliseconds:0.0} ms");
        Console.Out.Flush();''')
sources[p]=s
p='tools/VintageRTX.RenderLab/RenderLabExplorerTests.cs'
s=sources[p]
s=once(s,'''        Assert.IsTrue(report.AverageGpuMilliseconds > 0.0);''','''        Assert.IsTrue(File.Exists(Path.Combine(output, "phase-timings.json")));
        Assert.AreEqual(60, report.BenchmarkFrames);
        Assert.IsTrue(report.AverageGpuMilliseconds > 0.0);''')
sources[p]=s
for name,s in sources.items(): (root/name).write_text(s,encoding='utf-8',newline='\n')
for name in ['StandaloneTransportPipeline.cs','StandalonePipelineTests.cs']:
 shutil.copyfile(staging/(name+'.txt'),root/'tools/VintageRTX.RenderLab'/name)
print('Applied explicit CI/RenderLab source recovery; unchanged game shader and all acceptance floors.')
