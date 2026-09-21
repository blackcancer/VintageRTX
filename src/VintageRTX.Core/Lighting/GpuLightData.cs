using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Lighting;

/// <summary>
/// Reusable staging of evaluated frames: two RGBA32F texels per light, relative position/radius
/// then linear RGB intensity. Candidate validation is transactional; the last valid packet survives.
/// </summary>
public sealed class GpuLightData
{
    public const int Width = 2;
    private float[] pixels = new float[8], scratch = new float[8];
    public LightFrame? SourceFrame { get; private set; }
    public CellId Anchor { get; private set; }
    public int Count { get; private set; }
    public int Height => pixels.Length / 8;
    public long PixelRevision { get; private set; }
    public ReadOnlySpan<float> Pixels => pixels;
    // These describe the LAST pixel revision, not the last Update call. An unchanged frame must
    // retain them so a consumer that has not uploaded that revision can still catch up correctly.
    public int ChangedRowStart { get; private set; }
    public int ChangedRowCount { get; private set; }

    public bool Update(LightFrame frame, CellId anchor)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (SourceFrame is { } previous && previous.World == frame.World
            && (frame.Frame < previous.Frame || frame.TimeSeconds < previous.TimeSeconds
                || (frame.Frame == previous.Frame && !ReferenceEquals(frame, previous))))
            throw new ArgumentException("Stale or conflicting light frame.", nameof(frame));
        if (SourceFrame is { } prior && anchor == Anchor && frame.SharesSamplesWith(prior))
        { SourceFrame = frame; return false; }
        int capacity = Height;
        while (capacity < frame.Samples.Length) capacity = checked(capacity * 2);
        int length = checked(capacity * 8);
        if (scratch.Length != length) scratch = new float[length];
        // Every active texel is overwritten below; clearing it first was redundant memory traffic.
        else scratch.AsSpan(frame.Samples.Length * 8).Clear();
        int offset = 0;
        foreach (LightSample sample in frame.Samples)
        {
            float x = (float)(sample.Position.X - anchor.X), y = (float)(sample.Position.Y - anchor.Y);
            float z = (float)(sample.Position.Z - anchor.Z), radius = (float)sample.Radius;
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
                || !float.IsFinite(radius) || radius < 0 || !LightDefinition.FiniteNonnegative(sample.Intensity))
                throw new ArgumentOutOfRangeException(nameof(frame), "Light cannot be represented in the GPU packet.");
            scratch[offset++] = x; scratch[offset++] = y; scratch[offset++] = z; scratch[offset++] = radius;
            scratch[offset++] = sample.Intensity.X; scratch[offset++] = sample.Intensity.Y;
            scratch[offset++] = sample.Intensity.Z; scratch[offset++] = 0;
        }
        bool resized = pixels.Length != scratch.Length;
        bool changed = resized || !pixels.AsSpan().SequenceEqual(scratch);
        if (changed)
        {
            int first = 0, end = capacity;
            if (!resized)
            {
                while (pixels.AsSpan(first * 8, 8).SequenceEqual(scratch.AsSpan(first * 8, 8))) first++;
                while (pixels.AsSpan((end - 1) * 8, 8).SequenceEqual(scratch.AsSpan((end - 1) * 8, 8))) end--;
            }
            (pixels, scratch) = (scratch, pixels);
            PixelRevision++; ChangedRowStart = first; ChangedRowCount = end - first;
        }
        Count = frame.Samples.Length; Anchor = anchor; SourceFrame = frame;
        return changed;
    }
}
