using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Independent projection round-trips and failure-state tests for mirror depth ownership.</summary>
[TestClass]
public sealed class MirrorDepthRegressionTests
{
    /// <summary>A finite oblique row is insufficient when an identity fixture makes rows dependent.</summary>
    [TestMethod]
    public void SingularObliqueCandidateIsRejectedAndOrdinaryPairRemainsUsable()
    {
        double[] identity = Identity();
        double[] mirrored = Identity();
        mirrored[5] = -1;
        mirrored[13] = -2;
        double[] candidate = new double[16];
        Assert.IsFalse(EntityMirrorProjection.BuildObliqueMirrorProjection(identity, mirrored, -1, 0.01, candidate));
        CollectionAssert.AreEqual(new double[16], candidate);
        float[] forward = new float[16], inverse = new float[16];
        Assert.IsTrue(Pair(identity, forward, inverse));
        AssertRoundTrip(forward, inverse, [0.25, -0.5, -0.75, 1]);
    }

    /// <summary>The real perspective/oblique pair reconstructs the same reflected view-space point.</summary>
    [TestMethod]
    public void ObliquePerspectiveReconstructsDepthWithItsOwnInverse()
    {
        const double near = 0.1, far = 1024;
        double[] projection = new double[16];
        projection[0] = 1.1;
        projection[5] = 1.7;
        projection[10] = -(far + near) / (far - near);
        projection[11] = -1;
        projection[14] = -2 * far * near / (far - near);
        double[] mirrored = Identity();
        mirrored[5] = -1;
        mirrored[13] = -2;
        double[] oblique = new double[16];
        Assert.IsTrue(EntityMirrorProjection.BuildObliqueMirrorProjection(projection, mirrored, -1, 0.01, oblique));
        float[] forward = new float[16], inverse = new float[16];
        Assert.IsTrue(Pair(oblique, forward, inverse));
        foreach (double[] point in new[] { new double[] { 1, -3, -10, 1 }, [-2, -2.5, -30, 1], [4, -8, -60, 1] })
            AssertRoundTrip(forward, inverse, point);
        float[] ordinary = new float[16], ordinaryInverse = new float[16];
        Assert.IsTrue(Pair(projection, ordinary, ordinaryInverse));
        double[] wrong = Multiply(ordinaryInverse, Multiply(forward, [1, -3, -10, 1]));
        Assert.IsTrue(Math.Abs(wrong[2] / wrong[3] + 10) > 1,
            "The witness must detect an inverse from a different depth producer.");
    }

    /// <summary>Failure clears old outputs, so a rejected frame cannot reuse the last inverse.</summary>
    [TestMethod]
    public void InvalidProjectionClearsBothOutputsAndNeverMutatesSource()
    {
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            double[] source = Identity();
            source[0] = invalid;
            double[] retained = (double[])source.Clone();
            float[] forward = Enumerable.Repeat(7f, 16).ToArray();
            float[] inverse = Enumerable.Repeat(9f, 16).ToArray();
            Assert.IsFalse(Pair(source, forward, inverse));
            CollectionAssert.AreEqual(new float[16], forward);
            CollectionAssert.AreEqual(new float[16], inverse);
            CollectionAssert.AreEqual(retained, source);
        }
    }

    /// <summary>Checks singularity after conversion to the coefficients actually uploaded to GLSL.</summary>
    [TestMethod]
    public void FloatRoundingCannotPublishSingularOrInfiniteInverse()
    {
        double[] source = Identity();
        source[0] = 1e-60;
        float[] forward = new float[16], inverse = new float[16];
        Assert.IsFalse(Pair(source, forward, inverse));
        source[0] = 1e-40;
        Assert.IsFalse(Pair(source, forward, inverse));
        Assert.IsFalse(Pair(new double[15], forward, inverse));
        CollectionAssert.AreEqual(new float[16], forward);
        CollectionAssert.AreEqual(new float[16], inverse);
    }

    /// <summary>Invokes the production pair builder with independent scratch storage.</summary>
    private static bool Pair(double[] source, float[] forward, float[] inverse) =>
        EntityMirrorProjection.TryCreateDepthProjectionPair(source, forward, inverse, new double[16], new double[16]);

    /// <summary>Creates a column-major identity without using the production matrix library.</summary>
    private static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>Multiplies a matrix/vector using direct row dot products as the independent oracle.</summary>
    private static double[] Multiply(float[] matrix, double[] vector)
    {
        double[] output = new double[4];
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                output[row] += matrix[column * 4 + row] * vector[column];
        return output;
    }

    /// <summary>Projects to normalized device depth and reconstructs through the paired inverse.</summary>
    private static void AssertRoundTrip(float[] forward, float[] inverse, double[] point)
    {
        double[] clip = Multiply(forward, point);
        double[] ndc = clip.Select(value => value / clip[3]).ToArray();
        double[] reconstructed = Multiply(inverse, ndc);
        for (int axis = 0; axis < 3; axis++)
            Assert.AreEqual(point[axis], reconstructed[axis] / reconstructed[3], 0.0001);
    }
}
