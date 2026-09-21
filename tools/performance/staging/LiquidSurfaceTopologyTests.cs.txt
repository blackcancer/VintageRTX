using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Protects cache invalidation without changing the physical timestep or boundary conditions.</summary>
[TestClass]
public sealed class LiquidSurfaceTopologyTests
{
    /// <summary>Material coefficients shared by the small deterministic fixtures.</summary>
    private static readonly LiquidSurfaceDynamics Water = new(0.16f, 0.20f, 2.0f, 99.0f,
        0.0f, 0.04f, 0.072f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);

    /// <summary>Weather and repeated steps reuse topology; a batch of edits rebuilds it exactly once.</summary>
    [TestMethod]
    public void TopologyIsNotRebuiltForWindOrWaveMotion()
    {
        LiquidSurfaceSimulation simulation = new(0, 0, 9, 7, 0.5f);
        simulation.SetSurfaceCell(4, 3, 1, 1, Water, true);
        simulation.Advance(1.0f / 60, default);
        Assert.AreEqual(1, simulation.TopologyBuildCount);
        for (int i = 0; i < 30; i++) simulation.Advance(1.0f / 60, new(i * 0.03f, 0.4f, 0));
        Assert.AreEqual(1, simulation.TopologyBuildCount);
        simulation.SetSurfaceCell(3, 3, 1, 1, Water, true);
        simulation.SetSurfaceCell(5, 3, 1, 1, Water, true);
        simulation.ClearSurfaceCell(4, 3);
        simulation.Advance(1.0f / 60, default);
        Assert.AreEqual(2, simulation.TopologyBuildCount);
    }

    /// <summary>A removed surface clears both ping-pong buffers; later activation cannot resurrect waves.</summary>
    [TestMethod]
    public void RemovedAndReactivatedCellHasNoStaleWaveState()
    {
        LiquidSurfaceSimulation simulation = new(0, 0, 5, 5, 0.5f);
        LiquidSurfaceDynamics still = Water with { WindCoupling = 0 };
        simulation.SetSurfaceCell(2, 2, 1, 1, still, false);
        Assert.IsTrue(simulation.QueueImpact(1.25, 1.25, 1, 2));
        simulation.Advance(1.0f / 60, default);
        Assert.AreNotEqual(0.0f, simulation.GetCellSample(2, 2).Height);
        simulation.ClearSurfaceCell(2, 2);
        simulation.Advance(1.0f / 30, default);
        Assert.AreEqual(0.0f, simulation.GetCellSample(2, 2).Height);
        simulation.SetSurfaceCell(2, 2, 1, 1, still, false);
        simulation.Advance(1.0f / 60, default);
        Assert.AreEqual(0.0f, simulation.GetCellSample(2, 2).Height);
        Assert.AreEqual(0.0f, simulation.GetCellSample(2, 2).Velocity);
    }

    /// <summary>Single-row, single-column, and one-cell domains retain reflecting edges and finite normals.</summary>
    /// <param name="width">Domain width in cells.</param>
    /// <param name="depth">Domain depth in cells.</param>
    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(1, 11)]
    [DataRow(13, 1)]
    public void NarrowDomainsRetainFiniteState(int width, int depth)
    {
        LiquidSurfaceSimulation simulation = new(0, 0, width, depth, 0.5f);
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++) simulation.SetSurfaceCell(x, z, 1, 1, Water, true);
        for (int frame = 0; frame < 40; frame++) simulation.Advance(1.0f / 60, new(0.3f, 0.2f, 1e-7f));
        float[] texture = new float[width * depth * 4];
        simulation.WriteGpuTexture(texture);
        Assert.IsTrue(texture.All(float.IsFinite));
        Assert.AreEqual(1, simulation.TopologyBuildCount);
    }
}
