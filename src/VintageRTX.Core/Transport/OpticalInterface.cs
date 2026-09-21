using System.Numerics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Transport;

public readonly record struct InterfaceDirections(DVec3 Reflected, DVec3? Transmitted, double Reflectance);

/// <summary>Reference geometry for smooth interfaces. Does not implement the game's water renderer.</summary>
public static class OpticalInterface
{
    /// <summary>
    /// Normal points into the INCIDENT medium. IOR ordering is explicit; it is not inferred from
    /// camera height. Directions follow ray propagation towards and away from the interface.
    /// Transmitted direction may be absent under total internal reflection.
    /// </summary>
    public static InterfaceDirections Evaluate(DVec3 incident, DVec3 incidentFacingNormal,
        double incidentIor, double transmittedIor)
    {
        if(!double.IsFinite(incidentIor) || !double.IsFinite(transmittedIor) || incidentIor<=0 || transmittedIor<=0)
            throw new ArgumentException("Invalid medium indices.");
        DVec3 i=incident.Normalized(), n=incidentFacingNormal.Normalized();
        double c=-DVec3.Dot(i,n);
        if(c < -1e-12) throw new ArgumentException("Normal must face the incident medium.");
        c=Math.Clamp(c,0,1);
        DVec3 reflected=Bsdf.Reflect(i,n);
        if(incidentIor==transmittedIor) return new(reflected,i,0);
        double eta=incidentIor/transmittedIor;
        if(!double.IsFinite(eta) || eta==0) throw new ArgumentException("Unrepresentable IOR ratio.");
        double sin2=eta*eta*Math.Max(0,1-c*c);
        if(sin2>=1) return new(reflected,null,1);
        DVec3 transmitted=(i*eta+n*(eta*c-Math.Sqrt(Math.Max(0,1-sin2)))).Normalized();
        return new(reflected,transmitted,Bsdf.DielectricFresnel(c,incidentIor,transmittedIor));
    }

    /// <summary>Unscattered Beer-Lambert transport in a uniform medium; not in-scattered light.</summary>
    public static Vector3 Transmittance(Vector3 extinctionPerMetre, double distanceMetres)
    {
        if(!LightDefinition.FiniteNonnegative(extinctionPerMetre)
            || !double.IsFinite(distanceMetres) || distanceMetres<0)
            throw new ArgumentOutOfRangeException(nameof(distanceMetres));
        return new((float)Math.Exp(-extinctionPerMetre.X*distanceMetres),
            (float)Math.Exp(-extinctionPerMetre.Y*distanceMetres),
            (float)Math.Exp(-extinctionPerMetre.Z*distanceMetres));
    }

    /// <summary>Reflects a point around a plane represented by an anchor and its normal.</summary>
    public static DVec3 ReflectPoint(DVec3 point, DVec3 anchor, DVec3 normal)
    {
        if(!point.IsFinite || !anchor.IsFinite) throw new ArgumentException("Nonfinite plane or point.");
        DVec3 n=normal.Normalized();
        return point-n*(2*DVec3.Dot(point-anchor,n));
    }
}
