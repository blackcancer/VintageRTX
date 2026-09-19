using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Rendering;

/// <summary>Detached dynamic source. SourceIndex is session-stable, not an engine array index.</summary>
internal readonly record struct TrackedEntityLight(int SourceIndex, long EntityId, bool LocalPlayer, VoxelLight Light);

/// <summary>
/// Reads loaded client entities independently of engine light/frustum selection. Does not register
/// extra engine lights, modify gameplay, resurrect extinguished torches or retain entity instances.
/// The existing backend represents one aggregate source per entity; exact animated light sockets
/// and independently traced multiple hands remain a separate geometry contract.
/// </summary>
internal sealed class EntityLightCollector
{
    private readonly ICoreClientAPI api;
    private readonly IReadOnlyDictionary<string, Newtonsoft.Json.Linq.JObject> catalog;
    private readonly Dictionary<long, int> identities = new();
    private readonly HashSet<long> seen = new();
    private readonly List<TrackedEntityLight> lights = new();
    private readonly HashSet<string> warned = new(StringComparer.Ordinal);
    private int nextIdentity = 1_000_000;

    /// <summary>Uses the same catalogue as placed block emitters.</summary>
    public EntityLightCollector(ICoreClientAPI api)
    {
        this.api = api;
        catalog = EmitterPhotometryCatalog.Load(api.Assets, api.Logger);
    }

    /// <summary>
    /// Collects one authoritative runtime aggregate per nearby entity on the client render thread.
    /// Item entities explicitly query their current resolved stack; a zero V stays extinguished.
    /// </summary>
    public IReadOnlyList<TrackedEntityLight> Collect(double cameraX, double cameraY, double cameraZ, float range)
    {
        lights.Clear(); seen.Clear();
        // A client world and its entity collection can be absent during startup,
        // teardown, or a headless render fixture. Engine light arrays remain a
        // separate fallback; do not dereference a half-initialized world here.
        IClientWorldAccessor? world = api.World;
        Entity? player = world?.Player?.Entity;
        var loadedEntities = world?.LoadedEntities;
        if (player?.Pos is null || loadedEntities is null)
        {
            identities.Clear();
            return lights;
        }
        double rangeSquared = (range + 4.0) * (range + 4.0);
        foreach (Entity entity in loadedEntities.Values)
        {
            if (entity is null || entity.Pos is null || entity.Pos.Dimension != player.Pos.Dimension) continue;
            double dx = entity.Pos.X - cameraX, dy = entity.Pos.Y - cameraY, dz = entity.Pos.Z - cameraZ;
            if (!double.IsFinite(dx + dy + dz) || dx * dx + dy * dy + dz * dz > rangeSquared) continue;
            try
            {
                ItemStack? source = null;
                byte[]? hsv;
                if (entity is EntityItem item)
                {
                    source = item.Itemstack;
                    if (source?.Collectible is null || source.StackSize <= 0) continue;
                    hsv = source.Collectible.GetLightHsv(api.World.BlockAccessor, null!, source);
                }
                else
                {
                    // EntityPlayer.LightHsv already includes the hands. Never add the hands on top.
                    hsv = entity.LightHsv;
                    if (entity is EntityAgent agent)
                    {
                        ItemStack? right = agent.RightHandItemSlot?.Itemstack;
                        ItemStack? left = agent.LeftHandItemSlot?.Itemstack;
                        byte[]? rh = right?.Collectible?.GetLightHsv(api.World.BlockAccessor, null!, right);
                        byte[]? lh = left?.Collectible?.GetLightHsv(api.World.BlockAccessor, null!, left);
                        int rv = rh is { Length: >= 3 } ? rh[2] : 0;
                        int lv = lh is { Length: >= 3 } ? lh[2] : 0;
                        if (rv > 0 || lv > 0)
                        {
                            source = rv >= lv ? right : left;
                            if (hsv is not { Length: >= 3 } || hsv[2] == 0) hsv = rv >= lv ? rh : lh;
                        }
                    }
                }
                AssetLocation? sourceCode = source?.Collectible?.Code ?? entity.Code;
                if (sourceCode is null || !EmitterAppearance.TryResolve(sourceCode,
                    source?.Collectible?.Attributes ?? entity.Properties?.Attributes, hsv, catalog, out EmitterAppearance appearance)) continue;
                seen.Add(entity.EntityId);
                if (!identities.TryGetValue(entity.EntityId, out int index))
                {
                    index = checked(nextIdentity++);
                    identities.Add(entity.EntityId, index);
                }
                float height = entity is EntityItem ? 0.10f : (float)Math.Max(0.1, entity.LocalEyePos.Y * 0.75);
                EmitterPhotometry p = appearance.Photometry;
                VoxelLight light = new((float)entity.Pos.X, (float)(entity.Pos.Y + height), (float)entity.Pos.Z,
                    appearance.Red, appearance.Green, appearance.Blue, p.LuminousIntensityCandela,
                    sourceCode.ToString(), [], p.SourceHalfWidthMetres,
                    p.SourceHalfHeightMetres, p.CutoffIlluminanceLux, p.Basis);
                lights.Add(new TrackedEntityLight(index, entity.EntityId, entity.EntityId == player.EntityId, light));
            }
            catch (Exception exception)
            {
                string code = entity.Code?.ToString() ?? entity.GetType().Name;
                if (warned.Add(code)) api.Logger.Warning("[VintageRTX] Entity emitter query failed for {0}: {1}", code, exception.Message);
            }
        }
        foreach (long id in identities.Keys.Where(id => !seen.Contains(id)).ToArray()) identities.Remove(id);
        // Deterministic bounded selection is downstream; don't depend on dictionary traversal order.
        lights.Sort((a, b) => a.SourceIndex.CompareTo(b.SourceIndex));
        return lights;
    }
}
