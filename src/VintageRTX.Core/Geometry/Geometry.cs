using System.Numerics;

namespace VintageRTX.Core.Geometry;

/// <summary>Double-precision world coordinates. Subtract a render origin before GPU conversion.</summary>
public readonly record struct DVec3(double X, double Y, double Z)
{
    public static DVec3 Zero => default;
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double LengthSquared => Dot(this, this);
    public double Length => Math.Sqrt(LengthSquared);
    public DVec3 Normalized() => Length > 0 && IsFinite ? this / Length : throw new ArgumentException("Invalid direction.");
    public double this[int axis] => axis switch { 0 => X, 1 => Y, 2 => Z, _ => throw new IndexOutOfRangeException() };
    public Vector3 RelativeTo(DVec3 origin) => new((float)(X - origin.X), (float)(Y - origin.Y), (float)(Z - origin.Z));
    public static DVec3 operator +(DVec3 a, DVec3 b) => new(a.X+b.X, a.Y+b.Y, a.Z+b.Z);
    public static DVec3 operator -(DVec3 a, DVec3 b) => new(a.X-b.X, a.Y-b.Y, a.Z-b.Z);
    public static DVec3 operator -(DVec3 a) => new(-a.X,-a.Y,-a.Z);
    public static DVec3 operator *(DVec3 a, double b) => new(a.X*b, a.Y*b, a.Z*b);
    public static DVec3 operator /(DVec3 a, double b) => new(a.X/b, a.Y/b, a.Z/b);
    public static double Dot(DVec3 a, DVec3 b) => a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    public static DVec3 Cross(DVec3 a, DVec3 b) => new(a.Y*b.Z-a.Z*b.Y, a.Z*b.X-a.X*b.Z, a.X*b.Y-a.Y*b.X);
    public static DVec3 Min(DVec3 a, DVec3 b) => new(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Min(a.Z,b.Z));
    public static DVec3 Max(DVec3 a, DVec3 b) => new(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y),Math.Max(a.Z,b.Z));
}

/// <summary>Distances are world units along a normalized direction; boundaries are explicit.</summary>
public readonly record struct Ray
{
    public DVec3 Origin { get; }
    public DVec3 Direction { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public Ray(DVec3 origin, DVec3 direction, double minimum = 0, double maximum = double.PositiveInfinity)
    {
        if (!origin.IsFinite || !direction.IsFinite || direction.LengthSquared <= 0
            || !double.IsFinite(minimum) || minimum < 0 || double.IsNaN(maximum) || maximum <= minimum)
            throw new ArgumentException("Invalid ray interval or coordinates.");
        Origin=origin; Direction=direction.Normalized(); Minimum=minimum; Maximum=maximum;
    }
    public DVec3 At(double distance) => Origin + Direction * distance;
}

public readonly record struct Bounds(DVec3 Minimum, DVec3 Maximum)
{
    public bool Intersect(in Ray ray, double limit, out double entry)
    {
        double near=ray.Minimum, far=Math.Min(ray.Maximum,limit);
        for (int axis=0; axis<3; axis++)
        {
            double d=ray.Direction[axis], o=ray.Origin[axis];
            if (d == 0)
            {
                if (o < Minimum[axis] || o > Maximum[axis]) { entry=0; return false; }
                continue;
            }
            double a=(Minimum[axis]-o)/d, b=(Maximum[axis]-o)/d;
            near=Math.Max(near,Math.Min(a,b)); far=Math.Min(far,Math.Max(a,b));
            if (near>far) { entry=0; return false; }
        }
        entry=near; return true;
    }
    public static Bounds Union(Bounds a, Bounds b) => new(DVec3.Min(a.Minimum,b.Minimum),DVec3.Max(a.Maximum,b.Maximum));
}

public readonly record struct SurfaceHit(double Distance, DVec3 Position, DVec3 GeometricNormal, int Material, int Primitive);

/// <summary>Exact mesh triangle, not a collision box substituted for a render mesh.</summary>
public readonly record struct Triangle
{
    public DVec3 A { get; }
    public DVec3 B { get; }
    public DVec3 C { get; }
    public int Material { get; }
    public Triangle(DVec3 a, DVec3 b, DVec3 c, int material)
    {
        if (!a.IsFinite || !b.IsFinite || !c.IsFinite || material<0 || DVec3.Cross(b-a,c-a).LengthSquared <= 1e-24)
            throw new ArgumentException("Invalid triangle.");
        A=a; B=b; C=c; Material=material;
    }
    public Bounds Bounds => new(DVec3.Min(A,DVec3.Min(B,C)),DVec3.Max(A,DVec3.Max(B,C)));
    public DVec3 Centroid => A + ((B-A)+(C-A))/3;
    public bool Intersect(in Ray ray, double limit, int primitive, out SurfaceHit hit)
    {
        hit=default;
        DVec3 e1=B-A,e2=C-A,p=DVec3.Cross(ray.Direction,e2);
        double det=DVec3.Dot(e1,p);
        if (det == 0) return false;
        DVec3 s=ray.Origin-A;
        double u=DVec3.Dot(s,p)/det;
        if(u<0 || u>1) return false;
        DVec3 q=DVec3.Cross(s,e1);
        double v=DVec3.Dot(ray.Direction,q)/det;
        if(v<0 || u+v>1) return false;
        double t=DVec3.Dot(e2,q)/det;
        if(!double.IsFinite(t) || t<ray.Minimum || t>Math.Min(limit,ray.Maximum)) return false;
        hit=new(t,ray.At(t),DVec3.Cross(e1,e2).Normalized(),Material,primitive); return true;
    }
}
