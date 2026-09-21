using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Client;

/// <summary>
/// Initial public-API static geometry provider. Its supported subset is deliberately explicit:
/// plain opaque blocks with no random shape, procedural class, behaviour, extra LOD or block entity.
/// Other cells are UNSUPPORTED, never replaced by cubes. Entities/decor and textured cutouts remain
/// separate providers, so these data alone must not be advertised as complete world visibility.
/// </summary>
internal sealed class ClientGeometryCollector(ICoreClientAPI api) : IDisposable
{
    private readonly Dictionary<int,CellGeometry> templates=new();
    private readonly HashSet<string> warnings=new(StringComparer.Ordinal);
    private CellScene? scene;
    private GpuSceneData data=new();
    private SceneTextureSet? textures;
    private bool gpuFaulted;
    public CellSceneFrame? Frame { get; private set; }
    public int TemplateCount=>templates.Count;
    public int GpuTemplateCount=>data.TemplateCount;
    public bool GpuAllocated=>textures?.Ready==true && !gpuFaulted;
    public long LastUploadBytes=>textures?.LastUploadBytes??0;
    public long TotalUploadBytes=>textures?.TotalUploadBytes??0;
    public void Reset(WorldId world)
    {
        if(scene is null) scene=new(world);else scene.Reset(world);
        Frame=null;templates.Clear();warnings.Clear();data=new();gpuFaulted=false;
    }
    public void Clear()
    { scene=null;Frame=null;templates.Clear();data=new();gpuFaulted=false; }
    public void Invalidate(RegionId id)=>scene?.Invalidate(id);
    public void Unknown(BlockPos pos)=>scene?.Observe(new(pos.X,pos.Y,pos.Z),CellGeometry.Unknown);
    public void Observe(BlockPos pos,Block solid,Block fluid)
    {
        if(scene is null)return;
        CellGeometry value;
        // A solid/fluid combination needs a proper optical interface, not an opaque approximation.
        if(fluid is {Id:>0})value=CellGeometry.Unsupported;
        else if(solid.Id==0)value=CellGeometry.Empty;
        else if(templates.TryGetValue(solid.Id,out CellGeometry cached))value=cached;
        else if(!SupportedBlock(solid))value=CellGeometry.Unsupported;
        else
        {
            MeshData? mesh=api.TesselatorManager.GetDefaultBlockMesh(solid);
            if(mesh is null) { Unknown(pos);return; }
            try { value=CellGeometry.FromMesh(CopyMesh(mesh)); }
            catch(ArgumentException e)
            {
                value=CellGeometry.Unsupported;string code=solid.Code?.ToString()??solid.Id.ToString();
                if(warnings.Count<64 && warnings.Add(code)) api.Logger.Warning("[VintageRTX] Unresolved mesh {0}: {1}",code,e.Message);
            }
            templates[solid.Id]=value;
        }
        scene.Observe(new(pos.X,pos.Y,pos.Z),value);
    }
    internal static bool SupportedBlock(Block block)
    {
        if(block.GetType()!=typeof(Block) || block.Id==0 || block.RenderPass!=EnumChunkRenderPass.Opaque
            || block.HasAlternates || block.HasTiles || block.RandomizeRotations || block.RandomDrawOffset!=0
            || block.RandomSizeAdjust!=0 || !string.IsNullOrEmpty(block.EntityClass)
            || block.BlockBehaviors is {Length:>0} || block.BlockEntityBehaviors is {Length:>0}
            || block.Lod0Mesh is not null || block.Lod0Shape is not null
            || (block.DrawType!=EnumDrawType.Cube && block.DrawType!=EnumDrawType.JSON))return false;
        // Only surfaces declared opaque by the engine are eligible without alpha texture sampling.
        for(int face=0;face<6;face++)if(!block.SideOpaque[face])return false;
        return true;
    }
    internal static BlockMesh CopyMesh(MeshData mesh)
    {
        if(mesh.mode!=EnumDrawMode.Triangles || mesh.HasAnyWindModeSet
            || mesh.CustomBytes is not null || mesh.CustomFloats is not null || mesh.CustomInts is not null
            || mesh.CustomShorts is not null)throw new ArgumentException("Animated or custom mesh layout needs its own provider.");
        if(mesh.RenderPassCount>0)
        {
            if(mesh.RenderPassesAndExtraBits is null || mesh.RenderPassCount>mesh.RenderPassesAndExtraBits.Length)
                throw new ArgumentException("Invalid render-pass prefix.");
            for(int i=0;i<mesh.RenderPassCount;i++)
            { int pass=mesh.RenderPassesAndExtraBits[i];if(pass>=0 && (pass&1023)!=(int)EnumChunkRenderPass.Opaque)throw new ArgumentException("Nonopaque face."); }
        }
        return BlockMesh.CopyIndexed(mesh.xyz,mesh.VerticesCount,mesh.Indices,mesh.IndicesCount);
    }
    public void Publish()=>Frame=scene?.Capture();
    /// <summary>Must be called in the client render callback. This uploads data; it does not replace the image.</summary>
    public void Upload()
    {
        if(Frame is null || gpuFaulted)return;
        try
        {
            if(!data.Update(Frame)) { textures?.NoUpload();return; }
            textures??=new();textures.Upload(data);
        }
        catch(Exception e)
        {
            gpuFaulted=true;
            api.Logger.Warning("[VintageRTX] Rewrite geometry GPU cache unavailable: {0}. Native image remains intact.",e.Message);
        }
    }
    public void Dispose() { textures?.Dispose();textures=null;Clear(); }
}
