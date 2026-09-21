using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using VintageRTX.Rendering;

// The dense reference class is generated from an immutable, SHA-verified Git blob.
// Both solvers link the same physical model and use the same inputs; no in-game FPS claim.
internal static class Program
{
    private static readonly LiquidSurfaceDynamics Water = new(0.16f, 0.20f, 2.0f, 99.0f,
        0.0f, 0.04f, 0.072f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
    private static readonly LiquidSurfacePhysicalProperties Physics = new(998.2f, 0.001002f, 0.07275f);
    private static int comparedFrames;
    private static long comparedTexels;

    private static void Main(string[] args)
    {
        string reportPath = args.Single();
        foreach ((int w, int h) in new[] { (1,1), (1,11), (13,1), (17,13), (37,19), (128,128) })
            VerifyTrajectory(w, h);
        var measurements = new List<object>();
        foreach (int side in new[] { 0, 1, 8, 32, 64, 128 })
        {
            var before = new List<double>(5); var after = new List<double>(5);
            var setupBefore = new List<double>(5); var setupAfter = new List<double>(5);
            long allocationBefore = 0, allocationAfter = 0;
            for (int round = 0; round < 5; round++)
            {
                Pair pair = new(128, 128, side, false);
                long t = Stopwatch.GetTimestamp();
                pair.Before.Advance(1.0f / 120, default);
                setupBefore.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                t = Stopwatch.GetTimestamp(); pair.After.Advance(1.0f / 120, default);
                setupAfter.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                float[] packed = new float[128 * 128 * 4];
                LiquidSurfaceForcing wind = new(0.8f, 0.4f, 0);
                for (int i = 0; i < 30; i++)
                { pair.Before.Advance(1.0f / 60, wind); pair.After.Advance(1.0f / 60, wind); }
                // Alternate A/B order. All 240 measured updates retain fixed steps and packing.
                if ((round & 1) == 0) { MeasureBefore(); MeasureAfter(); }
                else { MeasureAfter(); MeasureBefore(); }
                pair.AssertEqual("benchmark final state");
                void MeasureBefore()
                {
                    long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 240; i++) { pair.Before.Advance(1.0f / 60, wind); pair.Before.WriteGpuTexture(packed); }
                    before.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / 240);
                    allocationBefore += GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
                void MeasureAfter()
                {
                    long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 240; i++) { pair.After.Advance(1.0f / 60, wind); pair.After.WriteGpuTexture(packed); }
                    after.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / 240);
                    allocationAfter += GC.GetAllocatedBytesForCurrentThread() - allocated;
                }
            }
            double a = Median(before), b = Median(after);
            measurements.Add(new { activeCells = side * side, totalCells = 128 * 128,
                beforeMilliseconds = a, afterMilliseconds = b, cpuCostRatio = b / a,
                samplesBefore = before, samplesAfter = after, allocationBefore, allocationAfter,
                firstStepBeforeMilliseconds = Median(setupBefore), firstStepAfterMilliseconds = Median(setupAfter) });
            Console.WriteLine($"Liquid {side*side}/16384: {a:F4} -> {b:F4} ms/update, ratio={b/a:F3}");
        }
        var result = new { scope = "CPU solver and RGBA packing only; excludes GPU upload, world queries and rendering",
            baselineCommit = "cdcc00ae42e9e4ddc279ac10cd5f4b72237b6ceb", baselineBlob = "ad2b4728ef4272d73024c53eb32b116e1d7446d9",
            runtime = RuntimeInformation.FrameworkDescription, platform = RuntimeInformation.OSDescription,
            cpuCount = Environment.ProcessorCount, comparedFrames, comparedTexels, bitwiseIdentical = true, measurements };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private static void VerifyTrajectory(int width, int height)
    {
        Pair p = new(width, height, int.MaxValue, true);
        for (int frame = 0; frame < 360; frame++)
        {
            int x = frame % width, z = (frame / 3) % height;
            if (frame % 17 == 0)
            { p.Before.ClearSurfaceCell(x,z); p.After.ClearSurfaceCell(x,z); }
            if (frame % 23 == 0) p.Configure(x,z, (byte)(frame % 3 + 1), frame % 2 == 0);
            if (frame % 11 == 0)
            {
                bool a = p.Before.QueueImpact((x + .5) * .5, (z + .5) * .5, 0.1f, 2, .3f, -.2f);
                bool b = p.After.QueueImpact((x + .5) * .5, (z + .5) * .5, 0.1f, 2, .3f, -.2f);
                if (a != b) throw new Exception("Impact admission differs");
            }
            LiquidSurfaceForcing forcing = new(frame % 7 == 0 ? 0 : .2f + frame / 20 * .03f,
                frame % 53 < 25 ? .3f : -.3f, frame % 40 < 12 ? 1e-7f : 0);
            float dt = frame % 61 == 0 ? .075f : frame % 3 == 0 ? 1f / 144 : 1f / 60;
            if (p.Before.Advance(dt, forcing) != p.After.Advance(dt, forcing)) throw new Exception("Fixed-step count differs");
            p.AssertEqual($"{width}x{height}, frame {frame}");
            comparedFrames++; comparedTexels += width * height;
        }
    }

    private sealed class Pair
    {
        internal readonly LiquidSurfaceSimulationReference Before;
        internal readonly LiquidSurfaceSimulation After;
        private readonly float[] before, after;
        private readonly int width, height;
        private readonly bool mixed;
        internal Pair(int width, int height, int side, bool mixed)
        {
            this.width=width; this.height=height; this.mixed=mixed;
            Before = new(0,0,width,height,.5f); After = new(0,0,width,height,.5f);
            before=new float[width*height*4]; after=new float[before.Length];
            for (int z=0; z<height; z++) for(int x=0; x<width; x++)
                if (mixed ? (x + z * 3) % 5 != 0 : x < side && z < side)
                    Configure(x,z,(byte)(mixed ? 1 + (x/5+z/7)%3 : 1), mixed);
        }
        internal void Configure(int x, int z, byte profile, bool rain)
        {
            LiquidSurfaceDynamics dynamics = mixed && profile == 3 ? Water with
                { BubbleRate = .2f, BubbleRadiusMinimum = .01f, BubbleRadiusMaximum = .04f,
                  BubbleRiseDuration = .15f, BubbleBurstStrength = .2f, BubbleEmissionBoost = .3f } : Water;
            float level = mixed ? (x/7%2)*.6f : 1;
            float depth = mixed ? .1f + (z%3)*.1f : float.PositiveInfinity;
            Before.SetSurfaceCell(x,z,level,profile,dynamics,Physics,rain,depth);
            After.SetSurfaceCell(x,z,level,profile,dynamics,Physics,rain,depth);
        }
        internal void AssertEqual(string where)
        {
            Before.WriteGpuTexture(before); After.WriteGpuTexture(after);
            if (!MemoryMarshal.AsBytes(before.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(after.AsSpan())))
            {
                int pixel=0; while(pixel<before.Length && BitConverter.SingleToInt32Bits(before[pixel])==BitConverter.SingleToInt32Bits(after[pixel])) pixel++;
                throw new Exception($"Liquid state differs at {where}: scalar {pixel}: {before[pixel]:R} != {after[pixel]:R}");
            }
            for(int z=0; z<height; z++) for(int x=0; x<width; x++)
                if (Before.GetCellSample(x,z) != After.GetCellSample(x,z)) throw new Exception("Velocity/sample differs: " + where);
            if (Before.TotalImpulseCount != After.TotalImpulseCount || Before.TotalBubbleSpawnCount != After.TotalBubbleSpawnCount
                || Before.ActiveBubbleCount != After.ActiveBubbleCount || Before.PendingImpulseCount != After.PendingImpulseCount
                || Before.DroppedEventCount != After.DroppedEventCount) throw new Exception("Event stream differs: " + where);
        }
    }
}
