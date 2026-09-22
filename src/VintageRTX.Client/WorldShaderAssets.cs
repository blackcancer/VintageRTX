using System.Text;
using VintageRTX.Core.Transport;
using Vintagestory.API.Common;

namespace VintageRTX.Client;

/// <summary>Transactional, reversible in-memory shader asset edits; never modifies the game installation.</summary>
internal sealed class WorldShaderAssets(IAssetManager assets) : IDisposable
{
    private sealed record Edit(IAsset Asset, byte[] Original, byte[] Patched);
    private readonly List<Edit> edits = new();
    public bool Installed => edits.Count == 8;
    public string? LastError { get; private set; }
    public bool Install()
    {
        if (Installed) return true;
        try
        {
            string fog = Read("game:shaderincludes/fogandlight.fsh");
            string scene = Read("vintagertx:shaderincludes/scene-query.glsl");
            string light = Read("vintagertx:shaderincludes/light-query.glsl");
            string material = Read("vintagertx:shaderincludes/material-query.glsl");
            string world = Read("vintagertx:shaderincludes/world-lighting.glsl");
            var candidate = new List<Edit>();
            foreach (string name in new[] { "chunkopaque", "chunktopsoil", "entityanimated", "standard" })
            {
                IAsset vertex = Get("game:shaders/" + name + ".vsh"), fragment = Get("game:shaders/" + name + ".fsh");
                WorldShaderPair pair = WorldShaderSource.Build(name, vertex.ToText(), fragment.ToText(), fog, scene, light, material, world);
                candidate.Add(new(vertex, vertex.Data, Encoding.UTF8.GetBytes(pair.Vertex)));
                candidate.Add(new(fragment, fragment.Data, Encoding.UTF8.GetBytes(pair.Fragment)));
            }
            // Validation of every stage pair has succeeded. No partly patched native pipeline.
            foreach (Edit edit in candidate) edit.Asset.Data = edit.Patched;
            edits.AddRange(candidate); LastError = null; return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        { LastError = e.Message; return false; }
    }
    private IAsset Get(string location) => assets.TryGet(new AssetLocation(location))
        ?? throw new InvalidDataException("Required shader asset is missing: " + location);
    private string Read(string location) => Get(location).ToText();
    public void Dispose()
    {
        foreach (Edit edit in edits)
            // Another mod may have changed it since installation. Restore only bytes still owned here.
            if (ReferenceEquals(edit.Asset.Data, edit.Patched)) edit.Asset.Data = edit.Original;
        edits.Clear();
    }
}
