using System.Numerics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Scene;

namespace VintageRTX.Core.Transport;

public readonly record struct DirectLightingResult(Vector3 Radiance, int Unresolved, int Blocked, int Traced);

/// <summary>
/// Direct reference using the existing BSDF, evaluated LightFrame and real CellSceneTracer.
/// No ambient floor, lit framebuffer, temporal history or whole-emitter-cell exemption.
/// </summary>
public static class DirectLightingReference
{
    public static DirectLightingResult Evaluate(DirectSurfaceFrame surfaces, int pixel, LightFrame lights,
        int finiteSamples = 8, double rayMinimum = 0.0001, int maximumCells = 256, Vector2 rotation = default)
    {
        ArgumentNullException.ThrowIfNull(surfaces); ArgumentNullException.ThrowIfNull(lights);
        if (surfaces.World != lights.World) throw new ArgumentException("Surface and light worlds differ.");
        if (finiteSamples is < 1 or > 64 || !double.IsFinite(rayMinimum) || rayMinimum < 0
            || maximumCells is < 1 or > 256 || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y))
            throw new ArgumentOutOfRangeException(nameof(finiteSamples));
        SurfaceReceiver receiver = surfaces.Receivers[pixel];
        if (!receiver.Present) return default;
        SurfaceMaterial material = surfaces.Material(receiver.MaterialIndex);
        DVec3 outgoing = surfaces.Camera - receiver.Position;
        if (outgoing.LengthSquared <= 0) return new(Vector3.Zero, 1, 0, 0);
        outgoing = outgoing.Normalized();
        if (DVec3.Dot(outgoing, receiver.GeometricNormal) <= 0 || DVec3.Dot(outgoing, receiver.ShadingNormal) <= 0)
            return new(Vector3.Zero, 1, 0, 0);
        // Ideal mirrors belong to a secondary/delta path, not an invented roughness floor.
        if (material.Kind == SurfaceKind.Conductor && material.Roughness == 0)
            return new(Vector3.Zero, 1, 0, 0);
        Vector3 sum = Vector3.Zero; int unresolved = 0, blocked = 0, traced = 0;
        foreach (LightSample light in lights.Samples)
        {
            if (light.Intensity == Vector3.Zero) continue;
            DVec3 delta = light.Position - receiver.Position;
            if (DVec3.Dot(delta, receiver.GeometricNormal) + light.Radius <= 0
                || DVec3.Dot(delta, receiver.ShadingNormal) + light.Radius <= 0) continue;
            int count = light.Radius > 0 ? finiteSamples : 1;
            Vector3 contribution = Vector3.Zero;
            for (int i = 0; i < count; i++)
            {
                Vector2 uv = Quadrature(i, count, rotation);
                if (!FiniteEmitterSampling.TrySample(light, receiver.Position, uv.X, uv.Y, out EmitterDirectionSample sample))
                { unresolved++; continue; }
                double cosine = DVec3.Dot(receiver.ShadingNormal, sample.Direction);
                if (cosine <= 0 || DVec3.Dot(receiver.GeometricNormal, sample.Direction) <= 0) continue;
                if (sample.Distance <= rayMinimum) { unresolved++; continue; }
                Vector3 bsdf = Bsdf.Evaluate(material, receiver.ShadingNormal, outgoing, sample.Direction);
                if (material.Kind == SurfaceKind.Diffuse) bsdf *= receiver.BaseColor;
                if (bsdf == Vector3.Zero) continue;
                SceneTrace hit = CellSceneTracer.Trace(surfaces.Scene,
                    new Ray(receiver.Position, sample.Direction, rayMinimum, sample.Distance), maximumCells);
                traced++;
                if (hit.Status == SceneTraceStatus.Hit) blocked++;
                else if (hit.Status != SceneTraceStatus.Clear) unresolved++;
                else contribution += sample.RadianceOverPdf * bsdf * (float)cosine;
            }
            sum += contribution / count;
        }
        if (!LightDefinition.FiniteNonnegative(sum)) throw new ArithmeticException("Direct radiance is not finite.");
        return new(sum, unresolved, blocked, traced);
    }

    public static Vector2 Quadrature(int ordinal, int count, Vector2 rotation = default)
    {
        if (count < 1 || ordinal < 0 || ordinal >= count || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y))
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        uint bits = (uint)ordinal;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        float x = (ordinal + .5f) / count + rotation.X;
        float y = bits * 2.3283064365386963e-10f + rotation.Y;
        return new(x - MathF.Floor(x), y - MathF.Floor(y));
    }
}
