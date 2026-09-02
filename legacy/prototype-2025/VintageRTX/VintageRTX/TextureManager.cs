using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.src
{
    /// <summary>
    /// Manages texture loading and generation for VintageRTX effects with automatic PBR detection
    /// </summary>
    public class TextureManager
    {
        private readonly ICoreClientAPI api;
        private readonly Dictionary<string, PBRTextureSet> pbrTextureSets;
        private readonly Dictionary<string, LoadedTexture> loadedTextures = new Dictionary<string, LoadedTexture>();

        /// <summary>
        /// Initializes a new instance of the TextureManager class
        /// </summary>
        /// <param name="api">The client API interface</param>
        public TextureManager(ICoreClientAPI api)
        {
            this.api = api;
            pbrTextureSets = new Dictionary<string, PBRTextureSet>();

            ScanForPBRTextures();
        }

        /// <summary>
        /// Scans all loaded textures and automatically detects PBR maps
        /// </summary>
        private void ScanForPBRTextures()
        {
            try
            {
                var assetManager = api.Assets;
                int foundNormals = 0, foundRoughness = 0, foundMetallic = 0;

                // CORRECTION: Utiliser GetMany qui retourne un dictionnaire
                var allAssets = assetManager.GetMany("textures/");

                foreach (var kvp in allAssets)
                {
                    var assetLocation = kvp.Key;
                    var asset = kvp.Value;
                    string assetPath = assetLocation.Path;

                    // Ignorer les fichiers PBR eux-mêmes pour éviter la duplication
                    if (assetPath.EndsWith("_n.png") || assetPath.EndsWith("_r.png") || assetPath.EndsWith("_m.png"))
                        continue;

                    // Extraire le nom de base de la texture
                    string baseName = Path.GetFileNameWithoutExtension(assetPath);
                    string baseDir = Path.GetDirectoryName(assetPath);
                    string domain = assetLocation.Domain;

                    // Créer un set PBR pour cette texture
                    var pbrSet = new PBRTextureSet
                    {
                        BaseName = baseName,
                        BaseTexture = asset
                    };

                    // Chercher les cartes PBR correspondantes
                    pbrSet.NormalMap = TryLoadPBRTexture(domain, baseDir, baseName, "_n");
                    pbrSet.RoughnessMap = TryLoadPBRTexture(domain, baseDir, baseName, "_r");
                    pbrSet.MetallicMap = TryLoadPBRTexture(domain, baseDir, baseName, "_m");

                    // Compter les cartes trouvées
                    if (pbrSet.HasNormalMap) foundNormals++;
                    if (pbrSet.HasRoughnessMap) foundRoughness++;
                    if (pbrSet.HasMetallicMap) foundMetallic++;

                    // Stocker le set même s'il n'a que la texture de base
                    pbrTextureSets[baseName] = pbrSet;
                }

                api.Logger.Notification($"[VintageRTX] Scanned PBR textures: {foundNormals} normal maps, {foundRoughness} roughness maps, {foundMetallic} metallic maps");
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[VintageRTX] Error scanning PBR textures: {ex}");
            }
        }

        /// <summary>
        /// Attempts to load a PBR texture with the given suffix
        /// </summary>
        private IAsset TryLoadPBRTexture(string domain, string directory, string baseName, string suffix)
        {
            try
            {
                string pbrPath = Path.Combine(directory, baseName + suffix + ".png").Replace('\\', '/');
                var location = new AssetLocation(domain, pbrPath);

                return api.Assets.TryGet(location);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets PBR information for a texture by name or path
        /// </summary>
        /// <param name="textureName">Texture name or asset path</param>
        /// <returns>PBR information with loaded texture IDs</returns>
        public TexturePBRInfo GetPBRInfo(string textureName)
        {
            // Extraire le nom de base si c'est un chemin complet
            string baseName = Path.GetFileNameWithoutExtension(textureName);

            // Essayer plusieurs variantes du nom
            PBRTextureSet pbrSet = null;

            // Essayer le nom exact
            if (pbrTextureSets.TryGetValue(baseName, out pbrSet))
            {
                return CreatePBRInfo(baseName, pbrSet);
            }

            // Essayer de trouver par correspondance partielle
            foreach (var kvp in pbrTextureSets)
            {
                if (kvp.Key.Contains(baseName) || baseName.Contains(kvp.Key))
                {
                    return CreatePBRInfo(kvp.Key, kvp.Value);
                }
            }

            // Retourner des informations par défaut
            return new TexturePBRInfo
            {
                TextureName = textureName,
                HasNormalMap = false,
                HasRoughnessMap = false,
                HasMetallicMap = false,
                NormalMapId = -1,
                RoughnessMapId = -1,
                MetallicMapId = -1
            };
        }

        /// <summary>
        /// Creates PBR info from a texture set, loading textures as needed
        /// </summary>
        private TexturePBRInfo CreatePBRInfo(string baseName, PBRTextureSet pbrSet)
        {
            var info = new TexturePBRInfo
            {
                TextureName = baseName,
                HasNormalMap = pbrSet.HasNormalMap,
                HasRoughnessMap = pbrSet.HasRoughnessMap,
                HasMetallicMap = pbrSet.HasMetallicMap
            };

            // Charger les textures et obtenir leurs IDs
            if (pbrSet.HasNormalMap)
            {
                info.NormalMapId = GetOrLoadTextureId(baseName + "_normal", pbrSet.NormalMap);
            }

            if (pbrSet.HasRoughnessMap)
            {
                info.RoughnessMapId = GetOrLoadTextureId(baseName + "_roughness", pbrSet.RoughnessMap);
            }

            if (pbrSet.HasMetallicMap)
            {
                info.MetallicMapId = GetOrLoadTextureId(baseName + "_metallic", pbrSet.MetallicMap);
            }

            return info;
        }

        /// <summary>
        /// Gets or loads a texture and returns its ID
        /// </summary>
        private int GetOrLoadTextureId(string cacheName, IAsset asset)
        {
            if (asset == null) return -1;

            try
            {
                // Vérifier si déjà en cache
                if (loadedTextures.TryGetValue(cacheName, out LoadedTexture cached))
                {
                    return cached.TextureId;
                }

                // Charger la texture
                LoadedTexture loadedTexture = null;
                var location = asset.Location;

                api.Render.GetOrLoadTexture(location, ref loadedTexture);

                if (loadedTexture != null && loadedTexture.TextureId > 0)
                {
                    loadedTextures[cacheName] = loadedTexture;
                    return loadedTexture.TextureId;
                }
            }
            catch (Exception ex)
            {
                api.Logger.Debug($"[VintageRTX] Could not load texture {cacheName}: {ex.Message}");
            }

            return -1;
        }

        /// <summary>
        /// Forces a rescan of PBR textures (useful for hot-reload)
        /// </summary>
        public void RescanPBRTextures()
        {
            pbrTextureSets.Clear();
            ScanForPBRTextures();
        }

        /// <summary>
        /// Gets statistics about loaded PBR textures
        /// </summary>
        public PBRStatistics GetStatistics()
        {
            var stats = new PBRStatistics();

            foreach (var pbrSet in pbrTextureSets.Values)
            {
                stats.TotalTextures++;
                if (pbrSet.HasNormalMap) stats.NormalMaps++;
                if (pbrSet.HasRoughnessMap) stats.RoughnessMaps++;
                if (pbrSet.HasMetallicMap) stats.MetallicMaps++;
            }

            return stats;
        }

        /// <summary>
        /// Cleans up loaded textures
        /// </summary>
        public void Dispose()
        {
            foreach (var loadedTexture in loadedTextures.Values)
            {
                loadedTexture?.Dispose();
            }
            loadedTextures.Clear();
            pbrTextureSets.Clear();

            api.Logger.Debug("[VintageRTX] TextureManager disposed");
        }
    }

    /// <summary>
    /// Represents a complete PBR texture set for a material
    /// </summary>
    public class PBRTextureSet
    {
        public string BaseName { get; set; }
        public IAsset BaseTexture { get; set; }
        public IAsset NormalMap { get; set; }
        public IAsset RoughnessMap { get; set; }
        public IAsset MetallicMap { get; set; }

        public bool HasNormalMap => NormalMap != null;
        public bool HasRoughnessMap => RoughnessMap != null;
        public bool HasMetallicMap => MetallicMap != null;
        public bool HasAnyPBR => HasNormalMap || HasRoughnessMap || HasMetallicMap;
    }

    /// <summary>
    /// Information about PBR textures available for a base texture
    /// </summary>
    public class TexturePBRInfo
    {
        /// <summary>Base texture name</summary>
        public string TextureName { get; set; }

        /// <summary>Whether a normal map is available</summary>
        public bool HasNormalMap { get; set; }

        /// <summary>Whether a roughness map is available</summary>
        public bool HasRoughnessMap { get; set; }

        /// <summary>Whether a metallic map is available</summary>
        public bool HasMetallicMap { get; set; }

        /// <summary>Normal map texture ID</summary>
        public int NormalMapId { get; set; } = -1;

        /// <summary>Roughness map texture ID</summary>
        public int RoughnessMapId { get; set; } = -1;

        /// <summary>Metallic map texture ID</summary>
        public int MetallicMapId { get; set; } = -1;

        /// <summary>Whether any PBR maps are available</summary>
        public bool HasAnyPBR => HasNormalMap || HasRoughnessMap || HasMetallicMap;
    }

    /// <summary>
    /// Statistics about PBR texture usage
    /// </summary>
    public class PBRStatistics
    {
        public int TotalTextures { get; set; }
        public int NormalMaps { get; set; }
        public int RoughnessMaps { get; set; }
        public int MetallicMaps { get; set; }
    }
}