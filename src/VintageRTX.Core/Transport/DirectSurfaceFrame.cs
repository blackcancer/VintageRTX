using System.Numerics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Transport;

/// <summary>A first visible opaque surface. BaseColor is an unlit linear texture multiplier.</summary>
public readonly record struct SurfaceReceiver(DVec3 Position, DVec3 ShadingNormal, DVec3 GeometricNormal,
    Vector3 BaseColor, int MaterialIndex, bool Present = true);

/// <summary>
/// Immutable surface ABI: bottom-left RGBA32F texels with one integer anchor, a captured geometry
/// revision and a separate material table. Light changes do not require another surface upload.
/// </summary>
public sealed class DirectSurfaceFrame
{
    public const int MaterialWidth = 3;
    private readonly float[] positions, normals, geometricNormals, colors, materials;
    private readonly SurfaceReceiver[] receivers;
    private readonly SurfaceMaterial[] definitions;
    public CellSceneFrame Scene { get; }
    public WorldId World => Scene.World;
    public long GeometryRevision => Scene.Revision;
    public CellId Anchor { get; }
    public DVec3 Camera { get; }
    public int Width { get; }
    public int Height { get; }
    public int MaterialCount => definitions.Length;
    public ReadOnlySpan<float> Positions => positions;
    public ReadOnlySpan<float> Normals => normals;
    public ReadOnlySpan<float> GeometricNormals => geometricNormals;
    public ReadOnlySpan<float> Colors => colors;
    public ReadOnlySpan<float> Materials => materials;
    public ReadOnlySpan<SurfaceReceiver> Receivers => receivers;
    public SurfaceMaterial Material(int index) => definitions[index];

    public DirectSurfaceFrame(CellSceneFrame scene, CellId anchor, DVec3 camera, int width, int height,
        ReadOnlySpan<SurfaceReceiver> surfaces, IReadOnlyList<SurfaceMaterial> materialDefinitions)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(materialDefinitions);
        if (width < 1 || height < 1 || width > 8192 || height > 8192
            || (long)width * height > 16_777_216 || surfaces.Length != (long)width * height)
            throw new ArgumentException("Invalid surface extent or active pixel count.");
        if (!camera.IsFinite || materialDefinitions.Count is < 1 or > 16384)
            throw new ArgumentException("Invalid camera or material table.");
        Scene = scene; Anchor = anchor; Camera = camera; Width = width; Height = height;
        Relative(camera, anchor);
        definitions = materialDefinitions.ToArray();
        foreach (SurfaceMaterial material in definitions) ArgumentNullException.ThrowIfNull(material);
        foreach (SurfaceReceiver surface in surfaces)
        {
            if (!surface.Present) continue;
            ValidateReceiver(surface, definitions.Length); Relative(surface.Position, anchor);
        }
        receivers = surfaces.ToArray();
        positions = new float[surfaces.Length * 4]; normals = new float[positions.Length];
        geometricNormals = new float[positions.Length]; colors = new float[positions.Length];
        materials = new float[definitions.Length * MaterialWidth * 4];
        for (int i = 0; i < definitions.Length; i++)
        {
            SurfaceMaterial m = definitions[i];
            Write(materials, i * 12, m.Reflectance, (float)m.Kind);
            Write(materials, i * 12 + 4, m.Eta, (float)m.Roughness);
            Write(materials, i * 12 + 8, m.K, 0);
        }
        for (int i = 0; i < receivers.Length; i++)
        {
            SurfaceReceiver r = receivers[i]; if (!r.Present) continue;
            Write(positions, i * 4, Relative(r.Position, anchor), 1);
            Write(normals, i * 4, ToFloat(r.ShadingNormal), r.MaterialIndex);
            Write(geometricNormals, i * 4, ToFloat(r.GeometricNormal), 1);
            Write(colors, i * 4, r.BaseColor * definitions[r.MaterialIndex].Reflectance, 1);
        }
    }
    public static void ValidateReceiver(in SurfaceReceiver receiver, int materialCount)
    {
        if (!receiver.Present) return;
        if (!receiver.Position.IsFinite || !Unit(receiver.ShadingNormal) || !Unit(receiver.GeometricNormal)
            || DVec3.Dot(receiver.ShadingNormal, receiver.GeometricNormal) <= 0
            || !LightDefinition.FiniteNonnegative(receiver.BaseColor)
            || receiver.BaseColor.X > 1 || receiver.BaseColor.Y > 1 || receiver.BaseColor.Z > 1
            || (uint)receiver.MaterialIndex >= (uint)materialCount)
            throw new ArgumentException("Invalid opaque receiver, material index or unlit reflectance.");
    }
    public static bool Unit(DVec3 normal) => normal.IsFinite && Math.Abs(normal.LengthSquared - 1) <= 1e-6;
    public static Vector3 Relative(DVec3 position, CellId anchor)
    {
        DVec3 p = position - anchor.Position;
        if (!p.IsFinite || Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) > 1_048_576)
            throw new ArgumentException("Surface lies outside the local-frame contract.");
        return ToFloat(p);
    }
    private static Vector3 ToFloat(DVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
    private static void Write(float[] target, int offset, Vector3 value, float w)
    { target[offset] = value.X; target[offset + 1] = value.Y; target[offset + 2] = value.Z; target[offset + 3] = w; }
}
