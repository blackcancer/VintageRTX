using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Lighting;

/// <summary>
/// Reusable staging of evaluated light frames. One row per source, two RGBA32F texels:
/// position relative to the integer anchor/radius, then scene-linear intensity/zero.
/// No clock, random sampling, geometry readiness or source selection is performed here.
/// </summary>
public sealed class GpuLightData
{
    public const int Width = 2;
    private float[] pixels = new float[8];
    private float[] scratch = new float[8];
    public LightFrame? SourceFrame { get; private set; }
    public CellId Anchor { get; private set; }
    public int Count { get; private set; }
    public int Height => pixels.Length / 8;
    public long PixelRevision { get; private set; }
    public ReadOnlySpan<float> Pixels => pixels;

    /// <summary>Validates a whole candidate before publishing it. Older same-world frames are rejected.</summary>
    public bool Update(LightFrame frame, CellId anchor)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (SourceFrame is { } previous && previous.World == frame.World
            && (frame.Frame < previous.Frame || frame.TimeSeconds < previous.TimeSeconds
                || (frame.Frame == previous.Frame && !ReferenceEquals(frame, previous))))
            throw new ArgumentException("Stale or conflicting light frame.", nameof(frame));
        int rows = Math.Max(1, frame.Samples.Length);
        int capacity = Height;
        while (capacity < rows) capacity = checked(capacity * 2);
        int length = checked(capacity * 8);
        if (scratch.Length != length) scratch = new float[length];
        else Array.Clear(scratch);
        int offset = 0;
        foreach (LightSample sample in frame.Samples)
        {
            float x = (float)(sample.Position.X - anchor.X);
            float y = (float)(sample.Position.Y - anchor.Y);
            float z = (float)(sample.Position.Z - anchor.Z);
            float radius = (float)sample.Radius;
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
                || !float.IsFinite(radius) || radius < 0 || !LightDefinition.FiniteNonnegative(sample.Intensity))
                throw new ArgumentOutOfRangeException(nameof(frame), "Light cannot be represented in the GPU packet.");
            scratch[offset++] = x; scratch[offset++] = y; scratch[offset++] = z; scratch[offset++] = radius;
            scratch[offset++] = sample.Intensity.X; scratch[offset++] = sample.Intensity.Y;
            scratch[offset++] = sample.Intensity.Z; scratch[offset++] = 0;
        }
        bool changed = pixels.Length != scratch.Length || !pixels.AsSpan().SequenceEqual(scratch);
        if (changed)
        {
            (pixels, scratch) = (scratch, pixels);
            PixelRevision++;
        }
        Count = frame.Samples.Length; Anchor = anchor; SourceFrame = frame;
        return changed;
    }
}
