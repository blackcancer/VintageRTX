using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Verifies the 1.22.7 fence compatibility prefix through real Harmony dispatch without loading a
/// world or acquiring a compile-time VSSurvivalMod reference.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FenceStackAwareTessellationGuardPatchTests
{
    /// <summary>Original engine offset restored after every test.</summary>
    private int originalUpperOffset;

    /// <summary>Resets static patch state and establishes a deterministic upper-neighbour offset.</summary>
    [TestInitialize]
    public void Initialize()
    {
        FenceStackAwareTessellationGuardPatch.Uninstall();
        originalUpperOffset = TileSideEnum.MoveIndex[TileSideEnum.Up];
        TileSideEnum.MoveIndex[TileSideEnum.Up] = 3;
    }

    /// <summary>Restores shared engine state even when a Harmony assertion fails.</summary>
    [TestCleanup]
    public void Cleanup()
    {
        FenceStackAwareTessellationGuardPatch.Uninstall();
        TileSideEnum.MoveIndex[TileSideEnum.Up] = originalUpperOffset;
    }

    /// <summary>Fails open when the survival type or exact 1.22.7 signature is unavailable.</summary>
    [TestMethod]
    public void InstallRejectsUnavailableAndMismatchedRuntimeContracts()
    {
        List<string> logs = [];
        ICoreClientAPI api = Api(logs);

        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.Install(api, _ => null));
        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.Install(
            api,
            name => name.EndsWith("BlockFenceStackAware", StringComparison.Ordinal)
                ? typeof(FenceContractFixture)
                : null));
        Assert.IsNull(FenceStackAwareTessellationGuardPatch.ResolveTarget(null));
        Assert.IsNull(FenceStackAwareTessellationGuardPatch.ResolveTarget(typeof(WrongContract)));
        Assert.IsNull(FenceStackAwareTessellationGuardPatch.ResolveTarget(typeof(FenceNeighborFixture)));
        Assert.IsTrue(logs.Any(message => message.Contains(
            "exact 1.22.7 method contract was not found",
            StringComparison.Ordinal)));
    }

    /// <summary>
    /// Installs a real Harmony prefix, preserves valid and null-slot calls, skips only unsafe array
    /// reads, bounds diagnostics, then proves uninstall restores the original method.
    /// </summary>
    [TestMethod]
    public void InstalledGuardSkipsOnlyOutOfBoundsNeighborAndUninstallsCleanly()
    {
        List<string> logs = [];
        ICoreClientAPI api = Api(logs);
        FenceContractFixture fixture = new();
        MeshData mesh = new();
        int[] light = [];
        Block[] blocks = new Block[8];
        BlockPos position = new(0, 0, 0);

        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.Install(
            api,
            ResolveFixtureType));
        Assert.IsNotNull(FenceStackAwareTessellationGuardPatch.ResolveTarget(
            typeof(FenceContractFixture)));

        fixture.OnJsonTesselation(ref mesh, ref light, position, blocks, 1);
        Assert.AreEqual(1, fixture.CallCount, "A valid index with a null neighbor slot remains native.");

        for (int index = 0; index < 20; index++)
        {
            fixture.OnJsonTesselation(ref mesh, ref light, position, blocks, 6);
        }

        Block[] nullChunk = null!;
        fixture.OnJsonTesselation(ref mesh, ref light, position, nullChunk, 0);
        Assert.AreEqual(1, fixture.CallCount);
        Assert.AreEqual(
            9,
            logs.Count(message => message.Contains("fence tessellation array read", StringComparison.OrdinalIgnoreCase)),
            "Eight detailed reports and one suppression notice are the entire bounded log budget.");

        FenceStackAwareTessellationGuardPatch.Uninstall();
        fixture.OnJsonTesselation(ref mesh, ref light, position, blocks, 6);
        Assert.AreEqual(2, fixture.CallCount, "Uninstall must restore the unguarded fixture method.");
    }

    /// <summary>
    /// Reproduces the real third IL access: the alternate hash selects an entry that the baked
    /// variant array does not contain. A cached mesh bypass and a matched array remain native.
    /// </summary>
    [TestMethod]
    public void InstalledGuardRejectsMismatchedBakedVariantOnlyOnCacheMiss()
    {
        List<string> logs = [];
        ICoreClientAPI api = Api(logs);
        FenceContractFixture fixture = new();
        MeshData mesh = new();
        int[] light = [];
        Block[] blocks = new Block[8];
        blocks[4] = new FenceNeighborFixture();
        BlockPos position = FindPositionSelectingAtLeast(3, 1);
        fixture.ConfigureWallTexture(alternateCount: 3, bakedVariantCount: 1);

        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.Install(api, ResolveFixtureType));
        fixture.OnJsonTesselation(ref mesh, ref light, position, blocks, 1);

        Assert.AreEqual(0, fixture.CallCount);
        Assert.IsTrue(logs.Any(message => message.Contains(
            "selectedVariant",
            StringComparison.Ordinal)));

        fixture.CacheMesh(position);
        fixture.OnJsonTesselation(ref mesh, ref light, position, blocks, 1);
        Assert.AreEqual(1, fixture.CallCount, "A cache hit never reaches the stock baked array read.");

        fixture.ClearMeshCache();
        fixture.ConfigureWallTexture(alternateCount: 3, bakedVariantCount: 3);
        fixture.OnJsonTesselation(ref mesh, ref light, position, blocks, 1);
        Assert.AreEqual(2, fixture.CallCount, "Matched alternate and baked arrays remain native.");

        fixture.ConfigureWallTexture(alternateCount: 0, bakedVariantCount: 0);
        fixture.OnJsonTesselation(ref mesh, ref light, position, blocks, 1);
        Assert.AreEqual(3, fixture.CallCount, "Non-index failures are deliberately not hidden.");
    }

    /// <summary>Covers null metadata, negative indexes, valid bounds, and overflow-safe arithmetic.</summary>
    [TestMethod]
    public void UpperNeighborValidationMatchesTheSingleStockArrayRead()
    {
        Block[] blocks = new Block[8];

        Assert.AreEqual(
            FenceTessellationUnsafeArray.MoveIndexTable,
            FenceStackAwareTessellationGuardPatch.ResolveUpperNeighbor(blocks, 0, [0, 0, 0, 0], out _));
        Assert.AreEqual(
            FenceTessellationUnsafeArray.ExtendedChunkNeighbor,
            FenceStackAwareTessellationGuardPatch.ResolveUpperNeighbor(blocks, 6, [0, 0, 0, 0, 3], out _));
        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.HasValidUpperNeighbor(null, 0, [0, 0, 0, 0, 3]));
        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.HasValidUpperNeighbor(blocks, 0, null));
        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.HasValidUpperNeighbor(blocks, 0, [0, 0, 0, 0]));
        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.HasValidUpperNeighbor(blocks, -4, [0, 0, 0, 0, 3]));
        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.HasValidUpperNeighbor(blocks, 4, [0, 0, 0, 0, 3]));
        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.HasValidUpperNeighbor(
            blocks,
            int.MaxValue,
            [0, 0, 0, 0, int.MaxValue]));
    }

    /// <summary>Covers every precursor and both bounds of the IL 00fa baked-variant access.</summary>
    [TestMethod]
    public void BakedVariantValidationUsesAlternateHashAgainstSupplyingArrayLength()
    {
        BlockPos position = FindPositionSelectingAtLeast(4, 2);
        CompositeTexture mismatch = Texture(alternateCount: 4, bakedVariantCount: 2);

        Assert.IsFalse(FenceStackAwareTessellationGuardPatch.HasValidBakedVariantAccess(
            position,
            mismatch,
            out int selected,
            out int available));
        Assert.IsTrue(selected >= 2);
        Assert.AreEqual(2, available);
        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.HasValidBakedVariantAccess(
            position,
            Texture(alternateCount: 4, bakedVariantCount: 4),
            out _,
            out _));
        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.HasValidBakedVariantAccess(
            position,
            Texture(alternateCount: 0, bakedVariantCount: 0),
            out _,
            out _));
        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.HasValidBakedVariantAccess(
            null,
            mismatch,
            out _,
            out _));
        Assert.IsTrue(FenceStackAwareTessellationGuardPatch.HasValidBakedVariantAccess(
            position,
            null,
            out _,
            out _));
    }

    /// <summary>Resolves the synthetic target and neighbor types by their production names.</summary>
    /// <param name="name">Runtime-qualified type name requested by the production installer.</param>
    /// <returns>The matching test contract, or null for unrelated names.</returns>
    private static Type? ResolveFixtureType(string name) => name switch
    {
        "Vintagestory.GameContent.BlockFenceStackAware" => typeof(FenceContractFixture),
        "Vintagestory.GameContent.BlockFence" => typeof(FenceNeighborFixture),
        _ => null
    };

    /// <summary>Creates texture arrays with independently controlled authored and baked lengths.</summary>
    /// <param name="alternateCount">Length used by the stock hash modulus.</param>
    /// <param name="bakedVariantCount">Length of the array actually indexed.</param>
    /// <returns>Minimal wall texture carrying both arrays.</returns>
    private static CompositeTexture Texture(int alternateCount, int bakedVariantCount) => new()
    {
        Alternates = new CompositeTexture[alternateCount],
        Baked = new BakedCompositeTexture
        {
            BakedVariants = new BakedCompositeTexture[bakedVariantCount]
        }
    };

    /// <summary>Finds a deterministic position whose stock hash exposes a shortened baked array.</summary>
    /// <param name="alternateCount">Hash modulus used by the stock method.</param>
    /// <param name="minimumIndex">Smallest selected index accepted by the caller.</param>
    /// <returns>A position selecting at least <paramref name="minimumIndex"/>.</returns>
    private static BlockPos FindPositionSelectingAtLeast(int alternateCount, int minimumIndex)
    {
        for (int x = 0; x < 1024; x++)
        {
            BlockPos candidate = new(x, 17, -23);
            if (GameMath.MurmurHash3Mod(
                    candidate.X,
                    candidate.Y,
                    candidate.Z,
                    alternateCount) >= minimumIndex)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a deterministic baked-variant witness.");
    }

    /// <summary>Builds a logger-only client API for deterministic installation diagnostics.</summary>
    /// <param name="logs">Destination for notification and warning format strings.</param>
    /// <returns>Minimal client API proxy consumed by the patch.</returns>
    private static ICoreClientAPI Api(List<string> logs)
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Warning) or nameof(ILogger.Notification))
            {
                logs.Add((string)arguments![0]!);
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        return RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_Logger"
                ? logger
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
    }

    /// <summary>Exact runtime method shape used to exercise Harmony without VSSurvivalMod.</summary>
    private sealed class FenceContractFixture : Block
    {
        /// <summary>Exact misspelled stock field name required by the audited runtime contract.</summary>
        private readonly Dictionary<string, MeshData> continousFenceMeches = [];
        /// <summary>Exact stock cache-key prefix required by the audited runtime contract.</summary>
        private readonly string cntCode = "fixture-";

        /// <summary>Gets the number of original calls that survived the prefix.</summary>
        internal int CallCount { get; private set; }

        /// <summary>Sets independently sized wall-texture arrays.</summary>
        /// <param name="alternateCount">Length used by the stock hash.</param>
        /// <param name="bakedVariantCount">Length of the array selected by that hash.</param>
        internal void ConfigureWallTexture(int alternateCount, int bakedVariantCount)
        {
            Textures = new Dictionary<string, CompositeTexture>
            {
                ["wall"] = Texture(alternateCount, bakedVariantCount)
            };
        }

        /// <summary>Adds the exact authored top-mesh cache key for one position.</summary>
        /// <param name="position">Position whose eight-way hash forms the key suffix.</param>
        internal void CacheMesh(BlockPos position)
        {
            int randomVariant = GameMath.MurmurHash3Mod(
                position.X,
                position.Y,
                position.Z,
                8) + 1;
            continousFenceMeches[cntCode + randomVariant.ToString()] = new MeshData();
        }

        /// <summary>Clears the synthetic stock mesh cache.</summary>
        internal void ClearMeshCache()
        {
            continousFenceMeches.Clear();
        }

        /// <summary>Models only the method signature and observable execution needed by the test.</summary>
        /// <param name="sourceMesh">Mutable source mesh contract.</param>
        /// <param name="lightRgbsByCorner">Mutable corner-light contract.</param>
        /// <param name="pos">Current block position.</param>
        /// <param name="chunkExtBlocks">Extended chunk block array.</param>
        /// <param name="extIndex3d">Current extended-chunk index.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void OnJsonTesselation(
            ref MeshData sourceMesh,
            ref int[] lightRgbsByCorner,
            BlockPos pos,
            Block[] chunkExtBlocks,
            int extIndex3d)
        {
            CallCount++;
        }
    }

    /// <summary>Minimal runtime type witness for the stock upper-neighbor <c>isinst</c> branch.</summary>
    private sealed class FenceNeighborFixture : Block
    {
    }

    /// <summary>Deliberately mismatched method contract rejected by exact resolution.</summary>
    private sealed class WrongContract
    {
        /// <summary>Uses the wrong parameter list and therefore must never be patched.</summary>
        internal void OnJsonTesselation()
        {
        }
    }
}
