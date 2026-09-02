using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Rendering;

/// <summary>
/// Removes reserved PBR suffixes from CompositeTexture wildcard expansions at
/// the last safe boundary before Vintage Story collects each albedo atlas.
/// </summary>
internal static class PbrTextureVariantSanitizer
{
    /// <summary>Removes sidecar references from block world and inventory composite graphs.</summary>
    /// <param name="blocks">Loaded block definitions; null elements are tolerated.</param>
    /// <returns>Total roots, alternates, tiles, and overlays removed.</returns>
    public static int SanitizeBlocks(IList<Block> blocks)
    {
        SanitizationContext context = new();
        foreach (Block block in blocks)
        {
            if (block is null)
            {
                continue;
            }

            SanitizeDictionary(block.Textures, context);
            SanitizeDictionary(block.TexturesInventory, context);
        }

        return context.Removed;
    }

    /// <summary>Removes sidecar references from item composite-texture graphs.</summary>
    /// <param name="items">Loaded item definitions; null elements are tolerated.</param>
    /// <returns>Total texture nodes removed.</returns>
    public static int SanitizeItems(IList<Item> items)
    {
        SanitizationContext context = new();
        foreach (Item item in items)
        {
            if (item is not null)
            {
                SanitizeDictionary(item.Textures, context);
            }
        }

        return context.Removed;
    }

    /// <summary>Removes sidecar references from client entity texture dictionaries.</summary>
    /// <param name="entities">Loaded entity definitions, including server-only entries.</param>
    /// <returns>Total texture nodes removed.</returns>
    public static int SanitizeEntities(IList<EntityProperties> entities)
    {
        SanitizationContext context = new();
        foreach (EntityProperties entity in entities)
        {
            if (entity?.Client is not null)
            {
                SanitizeDictionary(entity.Client.Textures, context);
            }
        }

        return context.Removed;
    }

    /// <summary>Sanitizes the baked root of a single composite after wildcard expansion.</summary>
    /// <param name="texture">Composite texture or <see langword="null"/>.</param>
    /// <returns>Total baked variants removed.</returns>
    public static int SanitizeCompositeTexture(CompositeTexture? texture)
    {
        SanitizationContext context = new();
        SanitizeBakedRoot(texture, context);
        return context.Removed;
    }

    /// <summary>Removes PBR roots before recursively filtering surviving composite graphs.</summary>
    /// <param name="textures">Texture-code dictionary owned by a game asset.</param>
    /// <param name="context">Reference-identity cycle guard and removal counter.</param>
    private static void SanitizeDictionary(
        IDictionary<string, CompositeTexture>? textures,
        SanitizationContext context)
    {
        if (textures is null)
        {
            return;
        }

        foreach (string key in textures
            .Where(pair => IsPbrComposite(pair.Value))
            .Select(pair => pair.Key)
            .ToArray())
        {
            textures.Remove(key);
            context.Removed++;
        }

        foreach (CompositeTexture texture in textures.Values)
        {
            SanitizeComposite(texture, context);
        }
    }

    /// <summary>Walks alternate, tile, overlay, and baked branches once per object identity.</summary>
    /// <param name="texture">Current composite node.</param>
    /// <param name="context">Traversal state shared across one asset collection.</param>
    private static void SanitizeComposite(
        CompositeTexture? texture,
        SanitizationContext context)
    {
        if (texture is null || !context.CompositeVisited.Add(texture))
        {
            return;
        }

        if (texture.Alternates is not null)
        {
            CompositeTexture[] retained = texture.Alternates
                .Where(candidate => !IsPbrComposite(candidate))
                .ToArray();
            context.Removed += texture.Alternates.Length - retained.Length;
            texture.Alternates = retained;
            foreach (CompositeTexture candidate in retained)
            {
                SanitizeComposite(candidate, context);
            }
        }

        if (texture.Tiles is not null)
        {
            CompositeTexture[] retained = texture.Tiles
                .Where(candidate => !IsPbrComposite(candidate))
                .ToArray();
            context.Removed += texture.Tiles.Length - retained.Length;
            texture.Tiles = retained;
            foreach (CompositeTexture candidate in retained)
            {
                SanitizeComposite(candidate, context);
            }
        }

        if (texture.BlendedOverlays is not null)
        {
            BlendedOverlayTexture[] retained = texture.BlendedOverlays
                .Where(overlay => !PbrSidecarAssetStore.HasPbrSuffix(overlay?.Base))
                .ToArray();
            context.Removed += texture.BlendedOverlays.Length - retained.Length;
            texture.BlendedOverlays = retained;
        }

        SanitizeBaked(texture.Baked, context);
    }

    /// <summary>Filters recursively baked wildcard variants and tiles without revisiting shared nodes.</summary>
    /// <param name="texture">Current baked node.</param>
    /// <param name="context">Traversal state shared across one asset collection.</param>
    private static void SanitizeBaked(
        BakedCompositeTexture? texture,
        SanitizationContext context)
    {
        if (texture is null || !context.BakedVisited.Add(texture))
        {
            return;
        }

        if (texture.BakedVariants is not null)
        {
            BakedCompositeTexture[] retained = texture.BakedVariants
                .Where(candidate => !IsPbrBaked(candidate))
                .ToArray();
            context.Removed += texture.BakedVariants.Length - retained.Length;
            texture.BakedVariants = retained;
            foreach (BakedCompositeTexture candidate in retained)
            {
                SanitizeBaked(candidate, context);
            }
        }

        if (texture.BakedTiles is not null)
        {
            BakedCompositeTexture[] retained = texture.BakedTiles
                .Where(candidate => !IsPbrBaked(candidate))
                .ToArray();
            context.Removed += texture.BakedTiles.Length - retained.Length;
            texture.BakedTiles = retained;
            foreach (BakedCompositeTexture candidate in retained)
            {
                SanitizeBaked(candidate, context);
            }
        }
    }

    /// <summary>
    /// Repairs Vintage Story's wildcard-root convention when the first baked candidate is a sidecar,
    /// promoting the first retained albedo before continuing recursive sanitation.
    /// </summary>
    /// <param name="composite">Composite whose baked root may be replaced.</param>
    /// <param name="context">Traversal state and removal counter.</param>
    private static void SanitizeBakedRoot(
        CompositeTexture? composite,
        SanitizationContext context)
    {
        BakedCompositeTexture? baked = composite?.Baked;
        if (baked is null)
        {
            return;
        }

        if (baked.BakedVariants is { Length: > 0 } variants)
        {
            BakedCompositeTexture[] retained = variants
                .Where(candidate => !IsPbrBaked(candidate))
                .ToArray();
            context.Removed += variants.Length - retained.Length;

            // Vintage Story stores the wildcard root as BakedVariants[0]. If
            // origin priority made a reserved sidecar the first match, promote
            // the first retained albedo so the manager cannot register the
            // sidecar as the root texture.
            if (IsPbrBaked(baked) && retained.Length > 0)
            {
                baked = retained[0];
                composite!.Baked = baked;
            }

            baked.BakedVariants = retained;
        }

        SanitizeBaked(baked, context);
    }

    /// <summary>Determines whether a composite root or its baked identity resolves to a sidecar.</summary>
    /// <param name="texture">Candidate composite.</param>
    /// <returns>Whether the node must not enter an albedo atlas.</returns>
    private static bool IsPbrComposite(CompositeTexture? texture)
    {
        return texture is not null
            && (PbrSidecarAssetStore.HasPbrSuffix(texture.Base)
                || IsPbrBaked(texture.Baked));
    }

    /// <summary>Checks baked root and all resolved filenames for reserved PBR suffixes.</summary>
    /// <param name="texture">Candidate baked node.</param>
    /// <returns>Whether any identity makes the node unsafe for albedo discovery.</returns>
    private static bool IsPbrBaked(BakedCompositeTexture? texture)
    {
        return texture is not null
            && (PbrSidecarAssetStore.HasPbrSuffix(texture.BakedName)
                || texture.TextureFilenames?.Any(PbrSidecarAssetStore.HasPbrSuffix) == true);
    }

    /// <summary>Per-operation reference-identity guards and exact removal cardinality.</summary>
    private sealed class SanitizationContext
    {
        /// <summary>Gets composite nodes already traversed, compared by object identity.</summary>
        public HashSet<CompositeTexture> CompositeVisited { get; } =
            new(ReferenceEqualityComparer.Instance);

        /// <summary>Gets baked nodes already traversed, compared by object identity.</summary>
        public HashSet<BakedCompositeTexture> BakedVisited { get; } =
            new(ReferenceEqualityComparer.Instance);

        /// <summary>Gets or sets the total number of discarded dictionary entries or child nodes.</summary>
        public int Removed { get; set; }
    }
}
