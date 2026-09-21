using System.Numerics;
using VintageRTX.Core.Geometry;

namespace VintageRTX.Core.Lighting;

/// <summary>
/// An incident direction and its importance weight, BEFORE visibility and the receiver BSDF.
/// RadianceOverPdf is L/pdf for a sphere, and I/distance^2 for a delta point light.
/// SolidAngle is zero for the delta measure; it must not be treated as an ordinary directional PDF.
/// </summary>
public readonly record struct EmitterDirectionSample(DVec3 Direction, double Distance,
    Vector3 RadianceOverPdf, double SolidAngle);

/// <summary>
/// Uniform solid-angle sampling of an outward, uniformly radiating sphere seen from outside.
/// Radius zero selects a point. The radius changes angular extent, not total source power:
/// L = I/(pi*R^2), Phi = 4*pi*I. Intensity uses the caller's documented linear relative units.
/// This is a source-shape approximation, not a volumetric combustion or flame simulation.
/// </summary>
public static class FiniteEmitterSampling
{
    /// <summary>
    /// Returns false when the receiver lies on/inside the sphere (unsupported medium), not proof
    /// of zero illumination. Caller supplies two uniform variates, independently of source time.
    /// All consumers use the already evaluated LightSample; no flicker is reevaluated here.
    /// </summary>
    public static bool TrySample(in LightSample light, DVec3 receiver, double u, double v,
        out EmitterDirectionSample sample)
    {
        sample = default;
        if (!receiver.IsFinite || !light.Position.IsFinite || !LightDefinition.FiniteNonnegative(light.Intensity)
            || !double.IsFinite(light.Radius) || light.Radius < 0
            || !double.IsFinite(u) || !double.IsFinite(v) || u < 0 || u >= 1 || v < 0 || v >= 1)
            throw new ArgumentOutOfRangeException(nameof(light), "Invalid emitter or sampling coordinates.");
        DVec3 delta = light.Position - receiver;
        double distanceSquared = delta.LengthSquared;
        if (!double.IsFinite(distanceSquared) || distanceSquared <= 0)
            return false;
        double distance = Math.Sqrt(distanceSquared);
        DVec3 axis = delta / distance;
        if (light.Radius == 0)
        {
            sample = new(axis, distance, Scale(light.Intensity, 1 / distanceSquared), 0);
            return true;
        }
        if (light.Radius >= distance) return false;
        double ratio = light.Radius / distance;
        double sinMaximumSquared = ratio * ratio;
        double cosMaximum = Math.Sqrt(Math.Max(0, 1 - sinMaximumSquared));
        // Rationalization retains tiny angular extents when 1-sqrt(1-R^2/d^2) cancels to zero.
        double oneMinusCosMaximum = sinMaximumSquared / (1 + cosMaximum);
        double oneMinusCosTheta = u * oneMinusCosMaximum;
        double cosTheta = 1 - oneMinusCosTheta;
        double sinThetaSquared = oneMinusCosTheta * (2 - oneMinusCosTheta);
        double sinTheta = Math.Sqrt(Math.Max(0, sinThetaSquared));
        DVec3 helper = Math.Abs(axis.Y) < .9 ? new(0, 1, 0) : new(1, 0, 0);
        DVec3 tangent = DVec3.Cross(helper, axis).Normalized();
        DVec3 bitangent = DVec3.Cross(axis, tangent);
        double phi = 2 * Math.PI * v;
        DVec3 direction = axis * cosTheta + tangent * (sinTheta * Math.Cos(phi))
            + bitangent * (sinTheta * Math.Sin(phi));
        // Nearest sphere intersection, rationalized to avoid subtracting nearly equal distances.
        double root = Math.Sqrt(Math.Max(0, sinMaximumSquared - sinThetaSquared));
        double near = distance * (1 - sinMaximumSquared) / (cosTheta + root);
        // L/pdf = I/(pi R^2) * 2pi(1-cosMaximum), without an unstable division by tiny R^2.
        double weight = 2 / (distanceSquared * (1 + cosMaximum));
        sample = new(direction, near, Scale(light.Intensity, weight), 2 * Math.PI * oneMinusCosMaximum);
        return true;
    }

    private static Vector3 Scale(Vector3 intensity, double scale)
    {
        Vector3 result = new((float)(intensity.X * scale), (float)(intensity.Y * scale), (float)(intensity.Z * scale));
        if (!LightDefinition.FiniteNonnegative(result))
            throw new OverflowException("Incident emission exceeds the finite RGB representation.");
        return result;
    }
}
