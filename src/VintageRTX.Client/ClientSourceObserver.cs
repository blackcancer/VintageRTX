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
using Vintagestory.API.MathTools;

namespace VintageRTX.Client;

/// <summary>
/// Public-API source observation, not a GPU renderer. No legacy hooks or native lights added.
/// Block and entity anchors are provisional until rendered emission geometry is qualified.
/// </summary>
internal sealed class ClientSourceObserver : IRenderer
{
    private readonly record struct Edit(long Epoch,int Dimension,int X,int Y,int Z);
    private readonly record struct ChunkNotice(long Epoch,int X,int InternalY,int Z);
    private readonly ICoreClientAPI api;
    private readonly ConcurrentDictionary<Edit,byte> edits=new();
    private readonly ConcurrentDictionary<ChunkNotice,byte> chunkNotices=new();
    private readonly DiscoveryWindow window;
    private readonly HashSet<LightId> blockSources=new(),entitySources=new(),seenEntities=new();
    private readonly HashSet<string> failedProviders=new(StringComparer.Ordinal);
    private readonly long tick;
    private LightRegistry? lights;
    private long epoch,frame;
    private int pollOffset;
    private volatile bool disposed;
    public LightFrame? CurrentFrame { get; private set; }
    public double RenderOrder=>0;
    public int RenderRange=>0;
    public ClientSourceObserver(ICoreClientAPI api)
    {
        this.api=api; window=new(SampleRegion,EvictRegion);
        api.Event.BlockChanged+=BlockChanged; api.Event.ChunkDirty+=ChunkDirty; api.Event.LeaveWorld+=LeaveWorld;
        tick=api.Event.RegisterGameTickListener(Tick,20);
        api.Event.RegisterRenderer(this,EnumRenderStage.Before,"vintagertx-rewrite-source-frame");
    }
    private void BlockChanged(BlockPos p,Block oldBlock)
    { if(!disposed) edits[new(Interlocked.Read(ref epoch),p.dimension,p.X,p.Y,p.Z)]=0; }
    private void ChunkDirty(Vec3i coordinate,IWorldChunk chunk,EnumChunkDirtyReason reason)
    { if(!disposed) chunkNotices[new(Interlocked.Read(ref epoch),coordinate.X,coordinate.Y,coordinate.Z)]=0; }
    private void LeaveWorld()
    {
        Interlocked.Increment(ref epoch); lights=null; CurrentFrame=null; window.Clear(); edits.Clear();chunkNotices.Clear();
        blockSources.Clear(); entitySources.Clear(); seenEntities.Clear(); failedProviders.Clear(); frame=0;pollOffset=0;
    }
    private void Tick(float dt)
    {
        if(disposed || api.World?.Player?.Entity is not { } player) return;
        int dimension=player.Pos.Dimension;
        if(lights is null || lights.World.Dimension!=dimension)
        { LeaveWorld(); lights=new(new WorldId(Guid.NewGuid(),dimension)); }
        DVec3 camera=new(player.Pos.X,player.Pos.Y,player.Pos.Z);
        window.MoveTo(DiscoveryWindow.At(camera));
        int count=0;
        foreach(Edit edit in edits.Keys)
        {
            if(count++>=128) break;
            if(edits.TryRemove(edit,out _) && edit.Epoch==epoch && edit.Dimension==dimension
                && window.Contains(DiscoveryWindow.At(new(edit.X,edit.Y,edit.Z))))
                ObserveBlock(edit.X,edit.Y,edit.Z,dimension);
        }
        count=0;
        foreach(ChunkNotice notice in chunkNotices.Keys)
        {
            if(count++>=64) break;
            if(!chunkNotices.TryRemove(notice,out _) || notice.Epoch!=epoch) continue;
            foreach(RegionId id in window.Active)
            {
                BlockPos p=new(id.X*8,id.Y*8,id.Z*8,dimension);
                int edge=GlobalConstants.ChunkSize;
                if((int)Math.Floor(p.X/(double)edge)==notice.X && (int)Math.Floor(p.InternalY/(double)edge)==notice.InternalY
                    && (int)Math.Floor(p.Z/(double)edge)==notice.Z) window.Invalidate(id);
            }
        }
        window.Drain(512,TimeSpan.FromMilliseconds(1));
        // State-dependent block entities may change emission without replacing the block id.
        LightId[] watch=blockSources.ToArray();
        long started=Stopwatch.GetTimestamp();
        for(int i=0;i<Math.Min(16,watch.Length) && Stopwatch.GetElapsedTime(started)<TimeSpan.FromMilliseconds(.25);i++)
        {
            LightId id=watch[pollOffset++%watch.Length]; ObserveBlock((int)id.X,(int)id.Y,(int)id.Z,dimension);
        }
        if(pollOffset>1000000) pollOffset=0;
        ObserveEntities(camera,dimension);
    }
    private void SampleRegion(RegionId id,int index)
    {
        if(lights is not null) ObserveBlock(id.X*8+index%8,id.Y*8+index/8%8,id.Z*8+index/64,lights.World.Dimension);
    }
    private void EvictRegion(RegionId region)
    {
        foreach(LightId id in blockSources.Where(id=>DiscoveryWindow.At(new(id.X,id.Y,id.Z))==region).ToArray())
        { lights?.Remove(id); blockSources.Remove(id); }
    }
    private void ObserveBlock(int x,int y,int z,int dimension)
    {
        if(lights is null) return;
        BlockPos pos=new(x,y,z,dimension);
        if(api.World.BlockAccessor.GetChunkAtBlockPos(pos) is null)
        {
            for(int channel=0;channel<2;channel++)
            { LightId stale=new(SourceKind.Block,x,y,z,channel,0); lights.Remove(stale); blockSources.Remove(stale); }
            return;
        }
        for(int channel=0;channel<2;channel++)
        {
            LightId id=new(SourceKind.Block,x,y,z,channel,0);
            Block block=api.World.BlockAccessor.GetBlock(pos,channel==0?BlockLayersAccess.Solid:BlockLayersAccess.Fluid);
            try
            {
                byte[]? hsv=block.GetLightHsv(api.World.BlockAccessor,pos);
                if(!Observe(id,block.Code?.ToString(),hsv,new(x+.5,y+.5,z+.5),false)) blockSources.Remove(id);
                else blockSources.Add(id);
            }
            catch(Exception e) { lights.Remove(id);blockSources.Remove(id);Warn(block.Code?.ToString(),e); }
        }
    }
    private void ObserveEntities(DVec3 camera,int dimension)
    {
        seenEntities.Clear();
        if(api.World.LoadedEntities is null)
        { foreach(LightId id in entitySources) lights!.Remove(id);entitySources.Clear();return; }
        foreach(Entity entity in api.World.LoadedEntities.Values)
        {
            if(entity?.Pos is null || entity.Pos.Dimension!=dimension) continue;
            DVec3 position=new(entity.Pos.X,entity.Pos.Y,entity.Pos.Z);
            if((position-camera).LengthSquared>64*64) continue;
            LightId id=new(SourceKind.Entity,entity.EntityId,0,0,0,0);
            try
            {
                byte[]? hsv; string? code; bool intrinsic;
                if(entity is EntityItem item && item.Itemstack?.Collectible is { } collectible)
                {
                    hsv=collectible.GetLightHsv(api.World.BlockAccessor,null!,item.Itemstack);
                    code=collectible.Code?.ToString();intrinsic=false;
                }
                else
                {
                    // The player getter already aggregates hands; never add them a second time.
                    hsv=entity.LightHsv; code=entity.Code?.ToString();intrinsic=true;
                }
                if(Observe(id,code,hsv,position,intrinsic)) seenEntities.Add(id);
            }
            catch(Exception e) { lights?.Remove(id);Warn(entity.Code?.ToString(),e); }
        }
        foreach(LightId stale in entitySources) if(!seenEntities.Contains(stale)) lights!.Remove(stale);
        entitySources.Clear();entitySources.UnionWith(seenEntities);
    }
    private bool Observe(LightId id,string? code,byte[]? hsv,DVec3 position,bool intrinsic)
    {
        if(lights is null) return false;
        if(code is null || hsv is not {Length:>=3} || hsv[2]==0 || !position.IsFinite)
        { lights.Remove(id);return false; }
        int rgb=ColorUtil.HsvToRgb(Math.Min(255,hsv[0]*ColorUtil.HueMul),Math.Min(255,hsv[1]*ColorUtil.SatMul),255);
        Vector3 linear=ColorSpace.Decode(new Vector3(ColorUtil.ColorR(rgb),ColorUtil.ColorG(rgb),ColorUtil.ColorB(rgb))/255f);
        float scale=hsv[2]/32f; // Explicitly provisional relative calibration, not measured SI photometry.
        lights.Upsert(id,new(position,linear*(scale*scale),EmissionProfiles.ForCode(code,intrinsic)));
        return true;
    }
    public void OnRenderFrame(float dt,EnumRenderStage stage)
    {
        if(!disposed && lights is not null) CurrentFrame=lights.Capture(++frame,api.InWorldEllapsedMilliseconds/1000.0);
    }
    public string Describe()=> $"VintageRTX R00: native rendering, no rewrite GPU passes. Sources={lights?.Count??0}, region jobs={window.Pending}, frame={frame}. Anchors provisional; weather binding pending.";
    private void Warn(string? code,Exception e)
    {
        string key=code??"unknown";
        if(failedProviders.Count<128 && failedProviders.Add(key)) api.Logger.Warning("[VintageRTX] Source provider {0}: {1}",key,e.Message);
    }
    public void Dispose()
    {
        if(disposed) return;disposed=true;
        api.Event.BlockChanged-=BlockChanged;api.Event.ChunkDirty-=ChunkDirty;api.Event.LeaveWorld-=LeaveWorld;
        api.Event.UnregisterGameTickListener(tick);api.Event.UnregisterRenderer(this,EnumRenderStage.Before);LeaveWorld();
    }
}
