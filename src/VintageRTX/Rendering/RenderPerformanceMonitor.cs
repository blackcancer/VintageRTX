using OpenTK.Graphics.OpenGL4;

namespace VintageRTX.Rendering;

/// <summary>
/// Maintains a bounded CPU frame-time window and asynchronous OpenGL timestamp ring. GPU queries
/// are sampled sparsely and never block the render thread waiting for availability.
/// </summary>
internal sealed class RenderPerformanceMonitor : IDisposable
{
    /// <summary>Ten-second history at 60 FPS used for average, p99, and jitter statistics.</summary>
    private const int FrameHistoryLength = 600;
    /// <summary>Timestamp pair count allowing the driver several frames to retire each query.</summary>
    private const int QueryRingLength = 4;
    /// <summary>GPU timing cadence in rendered frames; lowers query overhead without losing trends.</summary>
    private const int GpuQuerySampleInterval = 8;

    private readonly float[] frameTimesMilliseconds = new float[FrameHistoryLength];
    private readonly int[] startQueries = new int[QueryRingLength];
    private readonly int[] endQueries = new int[QueryRingLength];
    private readonly bool[] pendingQueries = new bool[QueryRingLength];

    private int frameHistoryIndex;
    private int frameHistoryCount;
    private int queryIndex;
    private int activeQueryIndex = -1;
    private int gpuQueryFrame;
    private bool queriesInitialized;
    private double smoothedGpuMilliseconds;
    private double latestGpuMilliseconds;

    /// <summary>Gets exponentially smoothed VintageRTX GPU time in milliseconds.</summary>
    public double SmoothedGpuMilliseconds => smoothedGpuMilliseconds;

    /// <summary>Clears CPU/GPU aggregates without destroying reusable OpenGL query objects.</summary>
    public void ResetStatistics()
    {
        Array.Clear(frameTimesMilliseconds);
        frameHistoryIndex = 0;
        frameHistoryCount = 0;
        smoothedGpuMilliseconds = 0.0;
        latestGpuMilliseconds = 0.0;
        gpuQueryFrame = 0;
    }

    /// <summary>Adds one plausible client frame duration to the circular CPU history.</summary>
    /// <param name="deltaTime">Elapsed frame time in seconds; non-finite, non-positive, and over-one-second samples are rejected.</param>
    public void RecordFrame(float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0.0f || deltaTime > 1.0f)
        {
            return;
        }

        frameTimesMilliseconds[frameHistoryIndex] = deltaTime * 1000.0f;
        frameHistoryIndex = (frameHistoryIndex + 1) % FrameHistoryLength;
        frameHistoryCount = Math.Min(frameHistoryCount + 1, FrameHistoryLength);
    }

    /// <summary>Starts a non-blocking timestamp interval when the cadence and ring slot allow it.</summary>
    /// <returns>Whether <see cref="EndGpuMeasurement"/> must close an active interval this frame.</returns>
    public bool TryBeginGpuMeasurement()
    {
        gpuQueryFrame++;
        if (gpuQueryFrame % GpuQuerySampleInterval != 0)
        {
            return false;
        }

        EnsureQueries();

        int slot = queryIndex;
        ResolveQueryIfAvailable(slot);
        if (pendingQueries[slot])
        {
            activeQueryIndex = -1;
            queryIndex = (queryIndex + 1) % QueryRingLength;
            return false;
        }

        GL.QueryCounter(startQueries[slot], QueryCounterTarget.Timestamp);
        activeQueryIndex = slot;
        return true;
    }

    /// <summary>Writes the end timestamp for the active interval and advances the query ring.</summary>
    public void EndGpuMeasurement()
    {
        if (activeQueryIndex < 0)
        {
            return;
        }

        GL.QueryCounter(endQueries[activeQueryIndex], QueryCounterTarget.Timestamp);
        pendingQueries[activeQueryIndex] = true;
        queryIndex = (activeQueryIndex + 1) % QueryRingLength;
        activeQueryIndex = -1;
    }

    /// <summary>Formats the current performance snapshot for the in-game status command.</summary>
    /// <returns>A collecting marker until at least one valid frame has been recorded.</returns>
    public string BuildStatus()
    {
        PerformanceSnapshot snapshot = CreateSnapshot();
        return snapshot.FrameCount == 0
            ? "fps=collecting"
            : snapshot.ToString();
    }

    /// <summary>Computes average FPS, p99-derived 1% low, population deviation, and smoothed GPU time.</summary>
    /// <returns>An immutable zero snapshot when the history is empty.</returns>
    public PerformanceSnapshot CreateSnapshot()
    {
        if (frameHistoryCount == 0)
        {
            return default;
        }

        float[] sorted = new float[frameHistoryCount];
        Array.Copy(frameTimesMilliseconds, sorted, frameHistoryCount);
        Array.Sort(sorted);

        double total = 0.0;
        for (int index = 0; index < sorted.Length; index++)
        {
            total += sorted[index];
        }

        double averageFrameTime = total / sorted.Length;
        int percentile99Index = Math.Clamp(
            (int)Math.Ceiling(sorted.Length * 0.99) - 1,
            0,
            sorted.Length - 1);
        double percentile99FrameTime = sorted[percentile99Index];

        double variance = 0.0;
        for (int index = 0; index < sorted.Length; index++)
        {
            double difference = sorted[index] - averageFrameTime;
            variance += difference * difference;
        }

        double frameTimeDeviation = Math.Sqrt(variance / sorted.Length);
        double averageFps = 1000.0 / averageFrameTime;
        double onePercentLowFps = 1000.0 / percentile99FrameTime;

        return new PerformanceSnapshot(
            frameHistoryCount,
            averageFps,
            onePercentLowFps,
            frameTimeDeviation,
            smoothedGpuMilliseconds);
    }

    /// <summary>Allocates timestamp query pairs lazily because no OpenGL work is legal before context startup.</summary>
    private void EnsureQueries()
    {
        if (queriesInitialized)
        {
            return;
        }

        for (int index = 0; index < QueryRingLength; index++)
        {
            startQueries[index] = GL.GenQuery();
            endQueries[index] = GL.GenQuery();
        }

        queriesInitialized = true;
    }

    /// <summary>Consumes a retired timestamp pair without stalling when the driver reports it unavailable.</summary>
    /// <param name="slot">Ring index whose pending state and query handles are inspected.</param>
    private void ResolveQueryIfAvailable(int slot)
    {
        if (!pendingQueries[slot])
        {
            return;
        }

        GL.GetQueryObject(
            endQueries[slot],
            GetQueryObjectParam.QueryResultAvailable,
            out int resultAvailable);
        if (resultAvailable == 0)
        {
            return;
        }

        GL.GetQueryObject(startQueries[slot], GetQueryObjectParam.QueryResult, out long startedAt);
        GL.GetQueryObject(endQueries[slot], GetQueryObjectParam.QueryResult, out long endedAt);
        pendingQueries[slot] = false;

        if (endedAt <= startedAt)
        {
            return;
        }

        latestGpuMilliseconds = (endedAt - startedAt) / 1_000_000.0;
        smoothedGpuMilliseconds = smoothedGpuMilliseconds <= 0.0
            ? latestGpuMilliseconds
            : smoothedGpuMilliseconds * 0.9 + latestGpuMilliseconds * 0.1;
    }

    /// <summary>Deletes owned OpenGL query handles; safe before initialization and after one disposal.</summary>
    public void Dispose()
    {
        if (!queriesInitialized)
        {
            return;
        }

        for (int index = 0; index < QueryRingLength; index++)
        {
            GL.DeleteQuery(startQueries[index]);
            GL.DeleteQuery(endQueries[index]);
        }

        queriesInitialized = false;
    }
}

/// <summary>
/// Immutable render-health sample. FPS metrics use CPU frame time; GPU milliseconds measure only
/// the instrumented VintageRTX pass and are exponentially smoothed.
/// </summary>
internal readonly record struct PerformanceSnapshot(
    int FrameCount,
    double AverageFps,
    double OnePercentLowFps,
    double JitterMilliseconds,
    double GpuMilliseconds)
{
    /// <summary>Formats invariant operational metrics with explicit millisecond units.</summary>
    /// <returns>Compact status text for logs and chat commands.</returns>
    public override string ToString()
    {
        return FormattableString.Invariant(
            $"fps={AverageFps:0.0}, 1%low={OnePercentLowFps:0.0}, jitter={JitterMilliseconds:0.00}ms, gpu={GpuMilliseconds:0.00}ms");
    }
}
