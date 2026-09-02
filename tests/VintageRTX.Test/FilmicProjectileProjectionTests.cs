using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Verifies exact world-to-framebuffer anchors used by projectile image validation.</summary>
[TestClass]
public sealed class FilmicProjectileProjectionTests
{
    /// <summary>Projects floating-origin coordinates with the documented top-left Y convention.</summary>
    [TestMethod]
    public void WorldPointProjectionProducesTopLeftFramebufferCoordinates()
    {
        double[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];

        Assert.IsTrue(FilmicDisplayRenderer.TryProjectWorldPoint(
            10.5,
            20.5,
            30.0,
            10.0,
            20.0,
            30.0,
            identity,
            identity,
            200,
            100,
            out double x,
            out double y));
        Assert.AreEqual(150.0, x, 0.001);
        Assert.AreEqual(25.0, y, 0.001);
    }

    /// <summary>Rejects incomplete matrices, non-positive clip W and points outside tolerance.</summary>
    [TestMethod]
    public void WorldPointProjectionRejectsInvalidOrInvisibleInputs()
    {
        double[] identity =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        ];
        double[] negativeW = (double[])identity.Clone();
        negativeW[15] = -1.0;

        Assert.IsFalse(FilmicDisplayRenderer.TryProjectWorldPoint(
            0, 0, 0, 0, 0, 0, [], identity, 200, 100, out _, out _));
        Assert.IsFalse(FilmicDisplayRenderer.TryProjectWorldPoint(
            0, 0, 0, 0, 0, 0, identity, negativeW, 200, 100, out _, out _));
        Assert.IsFalse(FilmicDisplayRenderer.TryProjectWorldPoint(
            2, 0, 0, 0, 0, 0, identity, identity, 200, 100, out _, out _));
        Assert.IsFalse(FilmicDisplayRenderer.TryProjectWorldPoint(
            0, 0, 0, 0, 0, 0, identity, identity, 0, 100, out _, out _));
    }
}
