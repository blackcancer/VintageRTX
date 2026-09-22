using System.Numerics;
using System.Text.Json;

namespace VintageRTX.Core.Transport;

/// <summary>
/// Explicit homogeneous BLOCK surface contract, after the game's native JSON patch/variant resolver.
/// No classification by color, glow, inventory stack or reflective flag. Textured multi-material
/// models require a per-primitive provider and must not opt into this whole-cell contract.
/// </summary>
public sealed record WorldSurfaceMaterial(SurfaceMaterial? Material)
{
    public const string Attribute = "vintageRtxMaterial";
    public static WorldSurfaceMaterial Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > 4096) throw new FormatException("vintageRtxMaterial: oversized definition.");
        try
        {
            using var doc = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Error("expected object");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in doc.RootElement.EnumerateObject())
            {
                if (!fields.TryAdd(field.Name, field.Value)) throw Error("duplicate " + field.Name);
                if (field.Name is not ("kind" or "roughness" or "eta" or "k")) throw Error("unknown " + field.Name);
            }
            if (!fields.TryGetValue("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                throw Error("kind is required");
            switch (kind.GetString())
            {
                case "native":
                    if (fields.Count != 1) throw Error("native accepts only kind");
                    return new((SurfaceMaterial?)null);
                case "diffuse":
                    if (fields.Count != 1) throw Error("diffuse accepts only kind");
                    return new(new SurfaceMaterial(SurfaceKind.Diffuse, Vector3.One, 1));
                case "conductor":
                    if (!fields.TryGetValue("roughness", out var r) || r.ValueKind != JsonValueKind.Number
                        || !r.TryGetDouble(out double roughness) || !double.IsFinite(roughness) || roughness < 0 || roughness > 1)
                        throw Error("roughness must be in [0,1]");
                    return new(new SurfaceMaterial(SurfaceKind.Conductor, Vector3.One, roughness, Rgb("eta", true), Rgb("k", false)));
                default: throw Error("kind must be native, diffuse or conductor");
            }
            Vector3 Rgb(string name, bool positive)
            {
                if (!fields.TryGetValue(name, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3)
                    throw Error(name + " must have three linear RGB components");
                Span<float> v = stackalloc float[3];
                for (int i = 0; i < 3; i++)
                {
                    if (a[i].ValueKind != JsonValueKind.Number || !a[i].TryGetSingle(out v[i]) || !float.IsFinite(v[i])
                        || (positive ? v[i] <= 0 : v[i] < 0) || v[i] > 32)
                        throw Error(name + " components outside the supported " + (positive ? "(0,32]" : "[0,32]") + " interval");
                }
                return new(v[0], v[1], v[2]);
            }
        }
        catch (JsonException e) { throw new FormatException("vintageRtxMaterial: invalid JSON.", e); }
    }
    private static FormatException Error(string message) => new("vintageRtxMaterial: " + message);
}
