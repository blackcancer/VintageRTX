using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Client;

/// <summary>
/// Shared public-API observations for configured emission and static geometry. World reads occur on
/// the owner thread; meshes are copied. GPU uploads happen only in Before. No legacy renderer is active.
/// </summary>
internal sealed class ClientSourceObserver : IRenderer
{
    private readonly record struct Edit(long Epoch, int Dimension, int X, int Y, int Z);
    private readonly record struct ChunkNotice(long Epoch, int X, int InternalY, int Z);
    private sealed record Observation(string Code, EmissionTarget Target, JsonObject? Attributes,
        DVec3 Position, Vector3 RuntimeIntensity);
    private readonly ICoreClientAPI api;
    private readonly EmissionAssetCatalog emissionAssets;
    private readonly ConcurrentDictionary<Edit, byte> edits = new();
    private readonly ConcurrentDictionary<ChunkNotice, byte> chunkNotices = new();
    private readonly DiscoveryWindow window;
    private readonly ClientGeometryCollector geometry;
    private readonly HashSet<LightId> blockSources = new(), entitySources = new(), seenEntities = new();
    private readonly Dictionary<LightId, Observation> observations = new();
    private readonly HashSet<string> failedProviders = new(StringComparer.Ordinal);
    private GpuLightData lightData = new();
    private LightTexture? lightTexture;
    private bool lightGpuFaulted;
    private LightId[] watchedBlocks = [];
    private bool watchDirty;
    private readonly long tick;
    private LightRegistry? lights;
    private long epoch, frame, appliedCatalogRevision = -1;
    private int pollOffset, reloadRequested;
    private volatile bool disposed;
    public LightFrame? CurrentFrame { get; private set; }
    public double RenderOrder => 0;
    public int RenderRange => 0;

    public ClientSourceObserver(ICoreClientAPI api, EmissionAssetCatalog emissionAssets)
    {
        this.api = api; this.emissionAssets = emissionAssets;
        geometry = new(api); window = new(SampleRegion, EvictRegion);
        api.Event.BlockChanged += BlockChanged; api.Event.ChunkDirty += ChunkDirty; api.Event.LeaveWorld += LeaveWorld;
        api.Event.ReloadShapes += ReloadAssets; api.Event.ReloadTextures += ReloadAssets;
        tick = api.Event.RegisterGameTickListener(Tick, 20);
        api.Event.RegisterRenderer(this, EnumRenderStage.Before, "vintagertx-rewrite-source-frame");
    }
    private void BlockChanged(BlockPos p, Block oldBlock)
    { if (!disposed) edits[new(Interlocked.Read(ref epoch), p.dimension, p.X, p.Y, p.Z)] = 0; }
    private void ChunkDirty(Vec3i coordinate, IWorldChunk chunk, EnumChunkDirtyReason reason)
    { if (!disposed) chunkNotices[new(Interlocked.Read(ref epoch), coordinate.X, coordinate.Y, coordinate.Z)] = 0; }
    private void ReloadAssets() => Interlocked.Exchange(ref reloadRequested, 1);
    private void LeaveWorld()
    {
        Interlocked.Increment(ref epoch); lights = null; CurrentFrame = null; window.Clear(); edits.Clear(); chunkNotices.Clear(); geometry.Clear();
        lightData = new(); lightTexture?.Invalidate(); lightGpuFaulted = false;
        blockSources.Clear(); entitySources.Clear(); seenEntities.Clear(); observations.Clear(); failedProviders.Clear();
        watchedBlocks = []; watchDirty = false; frame = 0; pollOffset = 0; appliedCatalogRevision = -1;
        emissionAssets.InvalidateResolutions();
    }
    private void Tick(float dt)
    {
        if (disposed || api.World?.Player?.Entity is not { Pos: not null } player) return;
        int dimension = player.Pos.Dimension;
        if (lights is null || lights.World.Dimension != dimension)
        { LeaveWorld(); lights = new(new WorldId(Guid.NewGuid(), dimension)); geometry.Reset(lights.World); }
        if (Interlocked.Exchange(ref reloadRequested, 0) != 0)
        { geometry.Reset(lights.World); window.Clear(); emissionAssets.InvalidateResolutions(); appliedCatalogRevision = -1; }
        DVec3 camera = new(player.Pos.X, player.Pos.Y, player.Pos.Z);
        window.MoveTo(DiscoveryWindow.At(camera));
        // Broad invalidations precede exact edits: same-tick notices cannot erase a newly observed cell.
        int count = 0;
        foreach (ChunkNotice notice in chunkNotices.Keys)
        {
            if (count++ >= 64) break;
            if (!chunkNotices.TryRemove(notice, out _) || notice.Epoch != epoch) continue;
            foreach (RegionId id in window.Active)
            {
                BlockPos p = new(id.X * 8, id.Y * 8, id.Z * 8, dimension); int edge = GlobalConstants.ChunkSize;
                if ((int)Math.Floor(p.X / (double)edge) == notice.X && (int)Math.Floor(p.InternalY / (double)edge) == notice.InternalY
                    && (int)Math.Floor(p.Z / (double)edge) == notice.Z)
                { geometry.Invalidate(id); window.Invalidate(id); }
            }
        }
        count = 0;
        foreach (Edit edit in edits.Keys)
        {
            if (count++ >= 128) break;
            if (edits.TryRemove(edit, out _) && edit.Epoch == epoch && edit.Dimension == dimension
                && window.Contains(DiscoveryWindow.At(new(edit.X, edit.Y, edit.Z))))
                ObserveBlock(edit.X, edit.Y, edit.Z, dimension);
        }
        window.Drain(512, TimeSpan.FromMilliseconds(1));
        // State-dependent potential emitters remain watched while off. Reuse the snapshot until
        // membership changes; pure emission polling does not rebuild meshes or invalidate geometry.
        if (watchDirty) { watchedBlocks = blockSources.ToArray(); watchDirty = false; }
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < Math.Min(16, watchedBlocks.Length)
            && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(.25); i++)
        {
            LightId id = watchedBlocks[pollOffset++ % watchedBlocks.Length];
            if (blockSources.Contains(id)) ObserveBlock((int)id.X, (int)id.Y, (int)id.Z, dimension, captureGeometry: false);
        }
        if (pollOffset > 1000000) pollOffset = 0;
        ObserveEntities(camera, dimension); geometry.Publish();
    }
    private void SampleRegion(RegionId id, int index)
    { if (lights is not null) ObserveBlock(id.X * 8 + index % 8, id.Y * 8 + index / 8 % 8, id.Z * 8 + index / 64, lights.World.Dimension); }
    private void RemoveSource(LightId id)
    { lights?.Remove(id); observations.Remove(id); }
    private void Unwatch(LightId id)
    { RemoveSource(id); if (blockSources.Remove(id)) watchDirty = true; }
    private void EvictRegion(RegionId region)
    {
        geometry.Invalidate(region);
        foreach (LightId id in blockSources.Where(id => DiscoveryWindow.At(new(id.X, id.Y, id.Z)) == region).ToArray()) Unwatch(id);
    }
    private void ObserveBlock(int x, int y, int z, int dimension, bool captureGeometry = true)
    {
        if (lights is null) return;
        BlockPos pos = new(x, y, z, dimension);
        if (api.World.BlockAccessor.GetChunkAtBlockPos(pos) is null)
        {
            geometry.Unknown(pos);
            for (int channel = 0; channel < 2; channel++) Unwatch(new(SourceKind.Block, x, y, z, channel, 0));
            return;
        }
        Block solid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Solid);
        Block fluid = api.World.BlockAccessor.GetBlock(pos, BlockLayersAccess.Fluid);
        if (captureGeometry)
        {
            try { geometry.Observe(pos, solid, fluid); }
            catch (Exception exception) { geometry.Unknown(pos); Warn("geometry:" + solid.Code, exception); }
        }
        for (int channel = 0; channel < 2; channel++)
        {
            LightId id = new(SourceKind.Block, x, y, z, channel, 0); Block block = channel == 0 ? solid : fluid;
            try
            {
                string? code = block.Code?.ToString();
                if (code is null) { Unwatch(id); continue; }
                EmissionSelection selection = emissionAssets.Resolve(code, EmissionTarget.Block, block.Attributes);
                byte[]? hsv = block.GetLightHsv(api.World.BlockAccessor, pos);
                bool candidate = Observe(id, code, hsv, new(x + .5, y + .5, z + .5), EmissionTarget.Block, block.Attributes);
                // CollectibleObject.LightHsv is ThreeBytes in the supported API, not byte[].
                bool potential = candidate || selection.ProfileId is not null || block.LightHsv[2] > 0;
                if (potential) { if (blockSources.Add(id)) watchDirty = true; }
                else Unwatch(id);
            }
            catch (Exception exception) { Unwatch(id); Warn(block.Code?.ToString(), exception); }
        }
    }
    private void ObserveEntities(DVec3 camera, int dimension)
    {
        seenEntities.Clear();
        if (api.World.LoadedEntities is null)
        { foreach (LightId id in entitySources) RemoveSource(id); entitySources.Clear(); return; }
        foreach (Entity entity in api.World.LoadedEntities.Values)
        {
            if (entity?.Pos is null || entity.Pos.Dimension != dimension) continue;
            DVec3 position = new(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
            if ((position - camera).LengthSquared > 64 * 64) continue;
            LightId id = new(SourceKind.Entity, entity.EntityId, 0, 0, 0, 0);
            try
            {
                byte[]? hsv; string? code; EmissionTarget target; JsonObject? attributes;
                if (entity is EntityItem item)
                {
                    if (item.Itemstack?.Collectible is not { } collectible || item.Itemstack.StackSize <= 0) continue;
                    hsv = collectible.GetLightHsv(api.World.BlockAccessor, null!, item.Itemstack);
                    code = collectible.Code?.ToString(); target = EmissionTarget.Item; attributes = collectible.Attributes;
                }
                else
                {
                    // The engine-owned player aggregate already includes both hands. Exact sockets are separate work.
                    hsv = entity.LightHsv; code = entity.Code?.ToString(); target = EmissionTarget.Entity;
                    attributes = entity.Properties?.Attributes;
                }
                if (Observe(id, code, hsv, position, target, attributes)) seenEntities.Add(id);
            }
            catch (Exception exception) { RemoveSource(id); Warn(entity.Code?.ToString(), exception); }
        }
        foreach (LightId stale in entitySources) if (!seenEntities.Contains(stale)) RemoveSource(stale);
        entitySources.Clear(); entitySources.UnionWith(seenEntities);
    }
    private bool Observe(LightId id, string? code, byte[]? hsv, DVec3 position, EmissionTarget target, JsonObject? attributes)
    {
        if (lights is null) return false;
        if (code is null || hsv is not { Length: >= 3 } || hsv[2] == 0 || !position.IsFinite)
        { RemoveSource(id); return false; }
        int rgb = ColorUtil.HsvToRgb(Math.Min(255, hsv[0] * ColorUtil.HueMul), Math.Min(255, hsv[1] * ColorUtil.SatMul), 255);
        Vector3 linear = ColorSpace.Decode(new Vector3(ColorUtil.ColorR(rgb), ColorUtil.ColorG(rgb), ColorUtil.ColorB(rgb)) / 255f);
        float scale = hsv[2] / 32f; // Provisional relative calibration, NOT measured SI photometry.
        var observation = new Observation(code, target, attributes, position, linear * (scale * scale));
        observations[id] = observation; Apply(id, observation);
        return true;
    }
    private void Apply(LightId id, Observation observation)
    {
        EmissionSelection selection = emissionAssets.Resolve(observation.Code, observation.Target, observation.Attributes);
        LightDefinition? definition = selection.CreateLight(observation.Position, observation.RuntimeIntensity);
        if (definition is null) lights!.Remove(id); else lights!.Upsert(id, definition);
    }
    public void OnRenderFrame(float dt, EnumRenderStage stage)
    {
        if (disposed || lights is null || stage != EnumRenderStage.Before) return;
        if (appliedCatalogRevision != emissionAssets.Revision)
        {
            // One owner-thread configuration transaction before the immutable frame is captured.
            // No world rescan or changed source positions/identities for a profile-only update.
            foreach ((LightId id, Observation observation) in observations) Apply(id, observation);
            appliedCatalogRevision = emissionAssets.Revision;
        }
        CurrentFrame = lights.Capture(++frame, api.InWorldEllapsedMilliseconds / 1000.0);
        // Emission is published independently and before geometry uploads. An empty/invalid scene
        // must never prevent a source from being observed, extinguished or sent to the GPU.
        if (!lightGpuFaulted && api.World?.Player?.Entity is { Pos: not null } player)
        {
            try
            {
                CellId anchor = new(checked((int)Math.Floor(player.Pos.X)), checked((int)Math.Floor(player.Pos.Y)),
                    checked((int)Math.Floor(player.Pos.Z)));
                lightData.Update(CurrentFrame, anchor);
                lightTexture ??= new(); lightTexture.Upload(lightData);
            }
            catch (Exception exception)
            {
                lightGpuFaulted = true; lightTexture?.Invalidate();
                api.Logger.Warning("[VintageRTX] Rewrite light GPU publication failed: {0}. Native image remains intact.", exception.Message);
            }
        }
        geometry.Upload();
    }
    public string Describe() => $"VintageRTX R01b: native image unchanged. Sources={lights?.Count ?? 0}, watched blocks={blockSources.Count}, pending regions={window.Pending}, published regions={geometry.Frame?.RegionCount ?? 0}, GPU templates={geometry.GpuTemplateCount}, GPU allocated={geometry.GpuAllocated}, geometry upload={geometry.LastUploadBytes} bytes, GPU light frame={lightTexture?.PublishedFrame?.Frame ?? -1}, light upload={lightTexture?.LastUploadBytes ?? 0} bytes, frame={frame}, emission catalog revision={emissionAssets.Revision}. Static opaque subset only; PBR image passes and animated sockets pending.";
    private void Warn(string? code, Exception exception)
    {
        string key = code ?? "unknown";
        if (failedProviders.Count < 128 && failedProviders.Add(key)) api.Logger.Warning("[VintageRTX] Source provider {0}: {1}", key, exception.Message);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        api.Event.BlockChanged -= BlockChanged; api.Event.ChunkDirty -= ChunkDirty; api.Event.LeaveWorld -= LeaveWorld;
        api.Event.ReloadShapes -= ReloadAssets; api.Event.ReloadTextures -= ReloadAssets;
        api.Event.UnregisterGameTickListener(tick); api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        LeaveWorld(); lightTexture?.Dispose(); lightTexture = null; geometry.Dispose();
    }
}
