using System.Numerics;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;

namespace VintageRTX.Core.Transport;

public enum SurfaceKind { Diffuse, Conductor }

/// <summary>Linear reflectance and complex RGB indices. Emission is a separate quantity.</summary>
public sealed record SurfaceMaterial
{
    public SurfaceKind Kind { get; }
    public Vector3 Reflectance { get; }
    public Vector3 Eta { get; }
    public Vector3 K { get; }
    public double Roughness { get; }
    public SurfaceMaterial(SurfaceKind kind,Vector3 reflectance,double roughness=0.5,Vector3 eta=default,Vector3 k=default)
    {
        if(!Enum.IsDefined(kind) || !LightDefinition.FiniteNonnegative(reflectance) || reflectance.X>1 || reflectance.Y>1 || reflectance.Z>1
            || !double.IsFinite(roughness) || roughness<0 || roughness>1
            || !LightDefinition.FiniteNonnegative(eta) || !LightDefinition.FiniteNonnegative(k)
            || kind==SurfaceKind.Conductor && (eta.X<=0 || eta.Y<=0 || eta.Z<=0))
            throw new ArgumentException("Invalid physical material parameters.");
        Kind=kind; Reflectance=reflectance; Roughness=roughness; Eta=eta; K=k;
    }
}

/// <summary>BSDF reference: Lambert and isotropic GGX conductor, with matched evaluation and PDF.</summary>
public static class Bsdf
{
    public static DVec3 Reflect(DVec3 incident,DVec3 normal) => incident-normal*(2*DVec3.Dot(incident,normal));
    public static double DielectricFresnel(double cosine,double incidentIor,double transmittedIor)
    {
        if(!double.IsFinite(cosine) || !double.IsFinite(incidentIor) || incidentIor<=0
            || !double.IsFinite(transmittedIor) || transmittedIor<=0) throw new ArgumentException("Invalid optical interface.");
        double c=Math.Clamp(Math.Abs(cosine),0,1), ratio=incidentIor/transmittedIor;
        double sin2=ratio*ratio*(1-c*c); if(sin2>=1) return 1;
        double ct=Math.Sqrt(Math.Max(0,1-sin2));
        double rs=(incidentIor*c-transmittedIor*ct)/(incidentIor*c+transmittedIor*ct);
        double rp=(transmittedIor*c-incidentIor*ct)/(transmittedIor*c+incidentIor*ct);
        if(c==0 && ct==0) return 0; // Identical media at grazing incidence.
        return (rs*rs+rp*rp)*0.5;
    }
    public static double ConductorFresnel(double cosine,double eta,double k)
    {
        double c=Math.Clamp(Math.Abs(cosine),0,1);
        if(c==0) return 1;
        double c2=c*c,s2=1-c2,e2=eta*eta,k2=k*k,t0=e2-k2-s2;
        double a2b2=Math.Sqrt(t0*t0+4*e2*k2),a=Math.Sqrt(Math.Max(0,(a2b2+t0)*0.5));
        double t1=a2b2+c2,t2=2*c*a;
        double rs=(t1-t2)/(t1+t2),t3=c2*a2b2+s2*s2,t4=t2*s2;
        return Math.Clamp(0.5*rs*(1+(t3-t4)/(t3+t4)),0,1);
    }
    public static Vector3 Fresnel(SurfaceMaterial m,double cosine) => new(
        (float)ConductorFresnel(cosine,m.Eta.X,m.K.X),
        (float)ConductorFresnel(cosine,m.Eta.Y,m.K.Y),
        (float)ConductorFresnel(cosine,m.Eta.Z,m.K.Z));
    public static double Distribution(double nh,double alpha)
    {
        if(nh<=0) return 0;
        double a2=alpha*alpha,d=nh*nh*(a2-1)+1;
        return a2/(Math.PI*d*d);
    }
    private static double Lambda(double cosine,double alpha)
    {
        double c2=cosine*cosine;
        return c2<=0 ? double.PositiveInfinity : (Math.Sqrt(1+alpha*alpha*(1-c2)/c2)-1)*0.5;
    }
    public static Vector3 Evaluate(SurfaceMaterial m,DVec3 normal,DVec3 outgoing,DVec3 incoming)
    {
        double nv=DVec3.Dot(normal,outgoing),nl=DVec3.Dot(normal,incoming);
        if(nv<=0 || nl<=0) return Vector3.Zero;
        if(m.Kind==SurfaceKind.Diffuse) return m.Reflectance/(float)Math.PI;
        if(m.Roughness==0) return Vector3.Zero; // A delta lobe is sampled, not evaluated as finite density.
        DVec3 sum=outgoing+incoming; if(sum.LengthSquared<1e-24) return Vector3.Zero;
        DVec3 h=sum.Normalized(); double alpha=Math.Max(1e-4,m.Roughness*m.Roughness);
        double d=Distribution(DVec3.Dot(normal,h),alpha),g=1/(1+Lambda(nv,alpha)+Lambda(nl,alpha));
        return Fresnel(m,DVec3.Dot(outgoing,h))*(float)(d*g/(4*nv*nl));
    }
    public static double Pdf(SurfaceMaterial m,DVec3 n,DVec3 wo,DVec3 wi)
    {
        double nl=DVec3.Dot(n,wi); if(nl<=0 || DVec3.Dot(n,wo)<=0) return 0;
        if(m.Kind==SurfaceKind.Diffuse) return nl/Math.PI;
        if(m.Roughness==0 || (wo+wi).LengthSquared<1e-24) return 0;
        DVec3 h=(wo+wi).Normalized(); double vh=DVec3.Dot(wo,h),nh=DVec3.Dot(n,h);
        return vh<=0 ? 0 : Distribution(nh,Math.Max(1e-4,m.Roughness*m.Roughness))*nh/(4*vh);
    }
    public static bool Sample(SurfaceMaterial m,DVec3 n,DVec3 wo,double u,double v,out DVec3 wi,out Vector3 weight)
    {
        if(u<0 || u>=1 || v<0 || v>=1 || !double.IsFinite(u+v)) throw new ArgumentOutOfRangeException(nameof(u));
        wi=default; weight=Vector3.Zero; if(DVec3.Dot(n,wo)<=0) return false;
        if(m.Kind==SurfaceKind.Conductor && m.Roughness==0)
        { wi=Reflect(-wo,n); weight=Fresnel(m,DVec3.Dot(n,wo)); return true; }
        DVec3 helper=Math.Abs(n.Y)<0.9?new(0,1,0):new(1,0,0);
        DVec3 tangent=DVec3.Cross(helper,n).Normalized(),bitangent=DVec3.Cross(n,tangent);
        double cosine;
        if(m.Kind==SurfaceKind.Diffuse) cosine=Math.Sqrt(1-u);
        else { double alpha=Math.Max(1e-4,m.Roughness*m.Roughness); cosine=1/Math.Sqrt(1+alpha*alpha*u/(1-u)); }
        double sine=Math.Sqrt(Math.Max(0,1-cosine*cosine)),phi=2*Math.PI*v;
        DVec3 direction=tangent*(sine*Math.Cos(phi))+bitangent*(sine*Math.Sin(phi))+n*cosine;
        wi=m.Kind==SurfaceKind.Diffuse?direction:Reflect(-wo,direction);
        double pdf=Pdf(m,n,wo,wi); if(pdf<=0) return false;
        weight=Evaluate(m,n,wo,wi)*(float)(Math.Max(0,DVec3.Dot(n,wi))/pdf);
        return true;
    }
}
