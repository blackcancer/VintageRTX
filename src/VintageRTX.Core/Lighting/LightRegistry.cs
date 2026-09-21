using System.Numerics;
using VintageRTX.Core.Geometry;

namespace VintageRTX.Core.Lighting;

public readonly record struct WorldId(Guid Session, int Dimension);
public enum SourceKind { Block, Entity, Weather, Extension }

/// <summary>Structural identity: never an array slot or proximity to another source.</summary>
public readonly record struct LightId(SourceKind Kind, long X, long Y, long Z, int Channel, long Incarnation)
{
    public ulong Seed => EmissionWaveform.Mix(unchecked((ulong)X)
        ^ EmissionWaveform.Mix(unchecked((ulong)Y)) ^ EmissionWaveform.Mix(unchecked((ulong)Z + 17))
        ^ EmissionWaveform.Mix((ulong)Kind + ((ulong)(uint)Channel << 8))
        ^ EmissionWaveform.Mix(unchecked((ulong)Incarnation)));
}

/// <summary>
/// Linear RGB radiant intensity in a documented relative scale, not invented lux/candela.
/// Intensity is the TOTAL for this aggregate, including ComponentCount. Radius is equivalent spherical
/// source size, never illumination range. Wick positions are a separate geometry contract.
/// </summary>
public sealed record LightDefinition
{
    public DVec3 Position { get; }
    public Vector3 Intensity { get; }
    public double Radius { get; }
    public EmissionProfile Profile { get; }
    public double BirthSeconds { get; }
    public int ComponentCount { get; }
    public LightDefinition(DVec3 position, Vector3 intensity, EmissionProfile profile,
        double birthSeconds = 0, double radius = 0, int componentCount = 1)
    {
        if (!position.IsFinite || !FiniteNonnegative(intensity) || !double.IsFinite(radius) || radius < 0
            || !double.IsFinite(birthSeconds) || componentCount is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(position));
        Position = position; Intensity = intensity; Radius = radius; ComponentCount = componentCount;
        Profile = profile ?? throw new ArgumentNullException(nameof(profile)); BirthSeconds = birthSeconds;
    }
    public static bool FiniteNonnegative(Vector3 c) => float.IsFinite(c.X) && float.IsFinite(c.Y)
        && float.IsFinite(c.Z) && c.X >= 0 && c.Y >= 0 && c.Z >= 0;
}

public readonly record struct LightSample(LightId Id, DVec3 Position, Vector3 Intensity, double Radius);
public readonly record struct LightRevisions(long Layout, long Emission);

/// <summary>One immutable evaluated frame is shared by primary and secondary transport.</summary>
public sealed class LightFrame
{
    private readonly LightSample[] samples;
    internal LightFrame(WorldId world, long frame, double time, LightRevisions revisions, LightSample[] samples)
    { World = world; Frame = frame; TimeSeconds = time; Revisions = revisions; this.samples = samples; }
    public WorldId World { get; }
    public long Frame { get; }
    public double TimeSeconds { get; }
    public LightRevisions Revisions { get; }
    public ReadOnlySpan<LightSample> Samples => samples;
}

/// <summary>Single-owner-thread registry. Geometry readiness, rain and GI are not prerequisites.</summary>
public sealed class LightRegistry
{
    private readonly Dictionary<LightId, LightDefinition> sources = new();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private long layout, emission, lastFrame = -1;
    private double lastTime = double.NegativeInfinity;
    public LightRegistry(WorldId world) => World = world;
    public WorldId World { get; private set; }
    public int Count => sources.Count;
    public LightRevisions Revisions => new(layout, emission);
    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("World observations must be marshalled to the registry owner thread.");
    }
    public void Reset(WorldId world)
    {
        AssertOwner(); World = world; sources.Clear(); layout++; emission++; lastFrame = -1; lastTime = double.NegativeInfinity;
    }
    public void Upsert(LightId id, LightDefinition definition)
    {
        AssertOwner(); ArgumentNullException.ThrowIfNull(definition);
        if (definition.Intensity == Vector3.Zero) { Remove(id); return; }
        if (sources.TryGetValue(id, out LightDefinition? previous))
        {
            if (previous == definition) return;
            if (previous.Position != definition.Position || previous.Radius != definition.Radius) layout++;
        }
        else layout++;
        // The block/chunk event invalidates its physical mesh independently. Component count alone
        // changes this aggregate's modulation, not its position or equivalent spherical geometry.
        sources[id] = definition; emission++;
    }
    public bool Remove(LightId id)
    {
        AssertOwner(); if (!sources.Remove(id)) return false;
        layout++; emission++; return true;
    }
    public LightFrame Capture(long frame, double seconds, double wind01 = 0)
    {
        AssertOwner();
        if (!double.IsFinite(seconds) || !double.IsFinite(wind01) || frame <= lastFrame || seconds < lastTime)
            throw new ArgumentOutOfRangeException(nameof(frame), "Frames and simulation time must be monotonic within a world.");
        var output = new LightSample[sources.Count]; int index = 0;
        foreach ((LightId id, LightDefinition light) in sources)
        {
            double modulation = EmissionGroupWaveform.Evaluate(light.Profile, id.Seed, seconds,
                light.BirthSeconds, light.ComponentCount, wind01);
            output[index++] = new(id, light.Position, light.Intensity * (float)modulation, light.Radius);
        }
        lastFrame = frame; lastTime = seconds;
        return new(World, frame, seconds, Revisions, output);
    }
}
