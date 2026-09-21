using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Core.Geometry;
using VintageRTX.Core.Transport;

namespace VintageRTX.Core.Tests;

[TestClass]
public sealed class OpticalInterfaceTests
{
    [TestMethod]
    public void IdenticalMediaHaveNoFresnelInterfaceEvenAtGrazing()
    {
        foreach(double c in new[]{0.0,1e-12,.1,.5,1})
        {
            Assert.AreEqual(0.0,Bsdf.DielectricFresnel(c,1.333,1.333));
            Assert.AreEqual(0.0,Bsdf.ConductorFresnel(c,1,0));
        }
    }
    [TestMethod]
    public void LosslessComplexIndexMatchesRealIndexAtAllAngles()
    {
        foreach(double eta in new[]{.7,1.0,1.5,2.4})
            for(int i=0;i<=100;i++)
                Assert.AreEqual(Bsdf.DielectricFresnel(i/100.0,1,eta),Bsdf.ConductorFresnel(i/100.0,eta,0),1e-12);
    }
    [TestMethod]
    public void ComplexIndexRejectsMalformedValues()
    {
        Assert.ThrowsException<ArgumentException>(()=>Bsdf.ConductorFresnel(.5,double.NaN,1));
        Assert.ThrowsException<ArgumentException>(()=>Bsdf.ConductorFresnel(.5,1,-1));
        Assert.ThrowsException<ArgumentException>(()=>Bsdf.ConductorFresnel(double.NaN,1,1));
    }
    [TestMethod]
    public void RefractionMatchesIndependentSnellLawAndReturnsUnitDirections()
    {
        double angle=Math.PI/6;
        DVec3 ray=new(Math.Sin(angle),0,-Math.Cos(angle));
        var result=OpticalInterface.Evaluate(ray,new(0,0,1),1,1.5);
        Assert.IsTrue(result.Transmitted.HasValue);
        DVec3 transmitted=result.Transmitted!.Value;
        Assert.AreEqual(Math.Sin(angle)/1.5,transmitted.X,1e-12);
        Assert.AreEqual(1.0,transmitted.Length,1e-12);
        Assert.AreEqual(Math.Cos(angle),result.Reflected.Z,1e-12);
        Assert.IsTrue(result.Reflectance>=0 && result.Reflectance<=1);
    }
    [TestMethod]
    public void TotalInternalReflectionDoesNotInventTransmission()
    {
        var result=OpticalInterface.Evaluate(new(.9,0,-Math.Sqrt(1-.81)),new(0,0,1),1.5,1);
        Assert.IsFalse(result.Transmitted.HasValue);Assert.AreEqual(1.0,result.Reflectance);
        Assert.IsTrue(result.Reflected.Z>0);
    }
    [TestMethod]
    public void IncidentMediumOrientationMustBeExplicit()
    {
        Assert.ThrowsException<ArgumentException>(()=>OpticalInterface.Evaluate(new(0,0,1),new(0,0,1),1,1.5));
        var same=OpticalInterface.Evaluate(new(1,0,0),new(0,0,1),1,1);
        Assert.AreEqual(new DVec3(1,0,0),same.Transmitted!.Value);Assert.AreEqual(0.0,same.Reflectance);
    }
    [TestMethod]
    public void OpticalDepthAddsAndZeroDistanceIsIdentity()
    {
        Vector3 sigma=new(.3f,.1f,.01f);
        Assert.AreEqual(Vector3.One,OpticalInterface.Transmittance(sigma,0));
        Vector3 split=OpticalInterface.Transmittance(sigma,2)*OpticalInterface.Transmittance(sigma,3);
        Assert.IsTrue(Vector3.Distance(split,OpticalInterface.Transmittance(sigma,5))<1e-7);
        Assert.AreEqual(Math.Exp(-.3f*5),OpticalInterface.Transmittance(sigma,5).X,1e-7);
        Assert.ThrowsException<ArgumentOutOfRangeException>(()=>OpticalInterface.Transmittance(sigma,-1));
    }
    [TestMethod]
    public void PlaneReflectionIsAnInvolutionAtLargeWorldCoordinates()
    {
        DVec3 anchor=new(1e10,1e10,1e10),point=anchor+new DVec3(1,2,3),normal=new(0,1,0);
        DVec3 reflected=OpticalInterface.ReflectPoint(point,anchor,normal);
        Assert.AreEqual(anchor+new DVec3(1,-2,3),reflected);
        Assert.AreEqual(point,OpticalInterface.ReflectPoint(reflected,anchor,normal));
    }
}
