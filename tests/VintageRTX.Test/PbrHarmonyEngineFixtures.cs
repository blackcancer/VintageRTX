using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

// These types deliberately use the runtime-qualified names resolved by
// PbrAssetDiscoveryPatch. They model only the atlas entry-point signatures
// needed to verify that the Harmony topology installs without requiring a
// complete game client inside the CPU test host.
namespace Vintagestory.Common
{
    /// <summary>
    /// Supports asset Manager within the deterministic VintageRTX test infrastructure.
    /// </summary>
    internal sealed class AssetManager
    {
        /// <summary>
        /// Executes the add External Assets step used by the deterministic asset Manager fixture.
        /// </summary>
        public void AddExternalAssets()
        {
        }
    }
}

namespace Vintagestory.Client.NoObf
{
    /// <summary>
    /// Supports block Texture Atlas Manager within the deterministic VintageRTX test infrastructure.
    /// </summary>
    internal sealed class BlockTextureAtlasManager
    {
        /// <summary>
        /// Executes the collect Textures step used by the deterministic block Texture Atlas Manager fixture.
        /// </summary>
        /// <param name="blocks">The blocks input used to configure this deterministic test path.</param>
        public void CollectTextures(IList<Block> blocks)
        {
        }
    }

    /// <summary>
    /// Supports item Texture Atlas Manager within the deterministic VintageRTX test infrastructure.
    /// </summary>
    internal sealed class ItemTextureAtlasManager
    {
        /// <summary>
        /// Executes the collect Textures step used by the deterministic item Texture Atlas Manager fixture.
        /// </summary>
        /// <param name="items">The items input used to configure this deterministic test path.</param>
        public void CollectTextures(IList<Item> items)
        {
        }
    }

    /// <summary>
    /// Supports entity Texture Atlas Manager within the deterministic VintageRTX test infrastructure.
    /// </summary>
    internal sealed class EntityTextureAtlasManager
    {
        /// <summary>
        /// Executes the collect Textures step used by the deterministic entity Texture Atlas Manager fixture.
        /// </summary>
        /// <param name="entityClasses">The entity Classes input used to configure this deterministic test path.</param>
        public void CollectTextures(List<EntityProperties> entityClasses)
        {
        }

        /// <summary>
        /// Executes the load Shape Textures step used by the deterministic entity Texture Atlas Manager fixture.
        /// </summary>
        public void LoadShapeTextures()
        {
        }
    }
}
