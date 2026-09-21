using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Lighting;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class TransportTests
{
    private static readonly DVec3 N=new(0,0,1);
    private static SurfaceMaterial Matte()=>new(SurfaceKind.Diffuse,Vector3.One);
    private static SurfaceMaterial Metal(double roughness=.5)=>new(SurfaceKind.Conductor,Vector3.Zero,roughness,new(.2f,.5f,1.2f),new(3.5f,2.5f,2.1f));
    private static Triangle Floor()=>new(new(-10,-10,0),new(10,-10,0),new(0,10,0),0);
    private static LightFrame Frame(int count=1,EmissionProfile? profile=null,double time=1)
    {
        var lights=new LightRegistry(new(Guid.NewGuid(),0));
        for(int i=0;i<count;i++) lights.Upsert(new(SourceKind.Block,i,0,0,0,0),new(new(0,0,2),new(4,2,1),profile??EmissionProfile.Steady));
        return lights.Capture(1,time);
    }
    [TestMethod]
    public void TriangleMatchesAnalyticPlaneDistanceAndNormal()
    {
        Assert.IsTrue(Floor().Intersect(new Ray(new(0,0,3),new(0,0,-7)),10,4,out var hit));
        Assert.AreEqual(3,hit.Distance,1e-12);Assert.AreEqual(N,hit.GeometricNormal);Assert.AreEqual(4,hit.Primitive);
        Assert.IsFalse(Floor().Intersect(new Ray(new(0,0,3),new(0,0,-1),0,2.5),10,4,out _));
    }
    [TestMethod]
    public void ParallelBoundsHaveNoNaNsAndRejectOutsideSlabs()
    {
        var bounds=new Bounds(new(0,0,0),new(1,1,1));
        Assert.IsTrue(bounds.Intersect(new Ray(new(0,.5,-1),N),10,out double t));Assert.AreEqual(1,t,1e-12);
        Assert.IsFalse(bounds.Intersect(new Ray(new(-.1,.5,-1),N),10,out _));
    }
    [TestMethod]
    public void CameraRelativeSubtractionPreservesSmallOffsets()
    {
        var origin=new DVec3(1e12,1e12,1e12);var p=origin+new DVec3(.25,.5,.75);
        Assert.AreEqual(new Vector3(.25f,.5f,.75f),p.RelativeTo(origin));
    }
    [TestMethod]
    public void BvhMatchesExhaustiveTriangleSearchAndOwnsItsInput()
    {
        var random=new Random(817);var triangles=new Triangle[64];
        for(int i=0;i<triangles.Length;i++)
        {
            DVec3 a=new(random.NextDouble()*8-4,random.NextDouble()*8-4,random.NextDouble()*8-4);
            triangles[i]=new(a,a+new DVec3(1,0,.2),a+new DVec3(0,1,.1),0);
        }
        var bvh=new TriangleBvh(triangles);
        for(int i=0;i<1000;i++)
        {
            Ray ray=new(new(0,0,8),new(random.NextDouble()-.5,random.NextDouble()-.5,-1));
            bool expected=false;double closest=ray.Maximum;
            for(int j=0;j<triangles.Length;j++) if(triangles[j].Intersect(ray,closest,j,out var h)) { expected=true;closest=h.Distance; }
            Assert.AreEqual(expected,bvh.Trace(ray,out var hit));if(expected) Assert.AreEqual(closest,hit.Distance,1e-10);
        }
        Array.Clear(triangles);Assert.AreEqual(64,bvh.TriangleCount);
    }
    [TestMethod]
    public void DielectricFresnelNormalGrazingAndTotalInternalReflection()
    {
        Assert.AreEqual(.04,Bsdf.DielectricFresnel(1,1,1.5),1e-12);
        Assert.AreEqual(1,Bsdf.DielectricFresnel(.1,1.5,1),1e-12);
        Assert.AreEqual(0,Bsdf.DielectricFresnel(.5,1,1),1e-12);
    }
    [TestMethod]
    public void ConductorFresnelMatchesIndependentNormalIncidenceFormula()
    {
        double eta=.4,k=3;
        double expected=((eta-1)*(eta-1)+k*k)/((eta+1)*(eta+1)+k*k);
        Assert.AreEqual(expected,Bsdf.ConductorFresnel(1,eta,k),1e-12);
    }
    [TestMethod]
    public void GgxReciprocityAndHemisphereRejection()
    {
        DVec3 a=new DVec3(.3,.2,1).Normalized(),b=new DVec3(-.5,.1,1).Normalized();var metal=Metal();
        Vector3 f=Bsdf.Evaluate(metal,N,a,b),g=Bsdf.Evaluate(metal,N,b,a);
        Assert.IsTrue(Vector3.Distance(f,g)<1e-6);Assert.AreEqual(Vector3.Zero,Bsdf.Evaluate(metal,N,a,-N));
    }
    [TestMethod]
    public void CosineWeightedLambertSampleWeightEqualsActualAlbedo()
    {
        var m=new SurfaceMaterial(SurfaceKind.Diffuse,new(.2f,.6f,.9f));
        for(int i=0;i<100;i++)
        {
            Assert.IsTrue(Bsdf.Sample(m,N,N,(i+.5)/100,.37,out var wi,out var weight));
            Assert.IsTrue(DVec3.Dot(N,wi)>0);Assert.IsTrue(Vector3.Distance(weight,m.Reflectance)<1e-6);
        }
    }
    [TestMethod]
    public void ConductorSampleAndPdfDescribeTheSameDistribution()
    {
        var m=Metal(.6);var wo=new DVec3(.4,.2,1).Normalized();int valid=0;
        for(int i=0;i<1000;i++) if(Bsdf.Sample(m,N,wo,(i+.5)/1000,(i*.61803398875)%1,out var wi,out var weight))
        {
            var expected=Bsdf.Evaluate(m,N,wo,wi)*(float)(DVec3.Dot(N,wi)/Bsdf.Pdf(m,N,wo,wi));
            Assert.IsTrue(Vector3.Distance(weight,expected)<1e-6);valid++;
        }
        Assert.IsTrue(valid>500);
    }
    [TestMethod]
    public void RoughConductorWhiteFurnaceCannotCreateEnergy()
    {
        var m=Metal(.65);Vector3 integral=Vector3.Zero;const int count=16384;
        for(int i=0;i<count;i++)
        {
            double z=(i+.5)/count,phi=2*Math.PI*((i*.61803398875)%1),r=Math.Sqrt(1-z*z);
            integral+=Bsdf.Evaluate(m,N,N,new(r*Math.Cos(phi),r*Math.Sin(phi),z))*(float)(2*Math.PI*z/count);
        }
        Assert.IsTrue(integral.X>0 && integral.Y>0 && integral.Z>0);
        Assert.IsTrue(integral.X<=1.001 && integral.Y<=1.001 && integral.Z<=1.001);
    }
    [TestMethod]
    public void PointLightOnLambertSurfaceMatchesInverseSquareAnalyticResult()
    {
        var tracer=new ReferenceTracer(new[]{Floor()},new[]{Matte()});
        Vector3 result=tracer.Trace(new(new(0,0,1),-N),Frame(),1,1);
        Assert.IsTrue(Vector3.Distance(new Vector3(4,2,1)/(float)(4*Math.PI),result)<1e-6);
    }
    [TestMethod]
    public void OpaqueBlockerRemovesLightInsteadOfInventingAmbient()
    {
        Triangle ceiling=new(new(-10,-10,1.5),new(0,10,1.5),new(10,-10,1.5),0);
        var tracer=new ReferenceTracer(new[]{Floor(),ceiling},new[]{Matte()});
        Assert.AreEqual(Vector3.Zero,tracer.Trace(new(new(0,0,1),-N),Frame(),1,1));
    }
    [TestMethod]
    public void EveryPointLightContributesNotJustFirstEight()
    {
        var tracer=new ReferenceTracer(new[]{Floor()},new[]{Matte()});Ray ray=new(new(0,0,1),-N);
        Assert.IsTrue(Vector3.Distance(tracer.Trace(ray,Frame(),1,1)*16,tracer.Trace(ray,Frame(16),1,1))<1e-5);
    }
    [TestMethod]
    public void MirrorTransportAndDirectTransportShareOneFlameEnvelope()
    {
        Triangle mirror=Floor();Triangle target=new(new(-10,-10,3),new(0,10,3),new(10,-10,3),1);
        var tracer=new ReferenceTracer(new[]{mirror,target},new[]{Metal(0),Matte()});Ray ray=new(new(0,0,1),new(.2,0,-1));
        LightFrame a=Frame(1,EmissionProfile.Torch,1.1),b=Frame(1,EmissionProfile.Torch,1.4);
        Assert.AreEqual(Vector3.Zero,tracer.Trace(ray,a,17,1));
        Vector3 ra=tracer.Trace(ray,a,17,2),rb=tracer.Trace(ray,b,17,2);
        Assert.IsTrue(ra.X>0 && rb.X>0);
        double ratio=b.Samples[0].Intensity.X/a.Samples[0].Intensity.X;
        Assert.AreEqual(ratio,rb.X/ra.X,1e-5);Assert.AreEqual(ratio,rb.Y/ra.Y,1e-5);
    }
    [TestMethod]
    public void UnsupportedFiniteAreaSourceIsNotSilentlyRenderedAsPoint()
    {
        var registry=new LightRegistry(new(Guid.NewGuid(),0));registry.Upsert(new(SourceKind.Block,0,0,0,0,0),new(new(0,0,2),Vector3.One,EmissionProfile.Steady,radius:.1));
        var tracer=new ReferenceTracer(new[]{Floor()},new[]{Matte()});var frame=registry.Capture(1,0);
        Assert.ThrowsException<NotSupportedException>(()=>tracer.Trace(new(new(0,0,1),-N),frame,1));
    }
}
