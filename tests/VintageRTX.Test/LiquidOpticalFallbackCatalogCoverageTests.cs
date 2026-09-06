using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using VintageRTX.Rendering;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>Covers client liquid-patch reconstruction success and containment failures.</summary>
[TestClass]
public sealed class LiquidOpticalFallbackCatalogCoverageTests
{
    /// <summary>Loads the real four-asset contract and contains a missing-asset failure.</summary>
    [TestMethod]
    public void LoadReportsSuccessAndContainsMissingAssets()
    {
        PatchInputs inputs = ReadProductionInputs();
        Dictionary<string, IAsset> catalog = new(StringComparer.Ordinal)
        {
            ["vintagertx:patches/liquid-optical-profiles.json"] = Asset(inputs.Optics),
            ["vintagertx:patches/liquid-physical-properties.json"] = Asset(inputs.Physics),
            ["vintagertx:patches/liquid-zz-measured-water-optics.json"] = Asset(inputs.Measured),
            ["vintagertx:config/liquid-client-bindings.json"] = Asset(inputs.Bindings),
        };
        int notifications = 0;
        int warnings = 0;
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
        {
            notifications += method.Name == nameof(ILogger.Notification) ? 1 : 0;
            warnings += method.Name == nameof(ILogger.Warning) ? 1 : 0;
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

        LiquidClientFallbackData loaded = LiquidOpticalFallbackCatalog.Load(
            AssetManager(catalog),
            logger);

        Assert.AreEqual(36, loaded.ProfilesByCodeRoot.Count);
        Assert.AreEqual(2, loaded.ContainersByCodeRoot.Count);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(0, warnings);

        LiquidClientFallbackData missing = LiquidOpticalFallbackCatalog.Load(
            AssetManager(new Dictionary<string, IAsset>(StringComparer.Ordinal)),
            logger);
        Assert.AreEqual(0, missing.ProfilesByCodeRoot.Count);
        Assert.AreEqual(0, missing.ContainersByCodeRoot.Count);
        Assert.AreEqual(1, warnings);
        Assert.ThrowsException<ArgumentNullException>(() =>
            LiquidOpticalFallbackCatalog.Load(null!));
    }

    /// <summary>Rejects every incomplete liquid binding before a profile can be published.</summary>
    [TestMethod]
    public void ParseRejectsSchemaPairProfileAndLiquidRootFailures()
    {
        PatchDocuments documents = ReadProductionDocuments();
        documents.Bindings["schemaVersion"] = 2;
        AssertInvalid(documents, "schema");

        documents = ReadProductionDocuments();
        FirstBinding(documents)["target"] = string.Empty;
        AssertInvalid(documents, "complete optics/physics");

        documents = ReadProductionDocuments();
        FirstBinding(documents)["target"] = "game:missing.json";
        AssertInvalid(documents, "complete optics/physics");

        documents = ReadProductionDocuments();
        string target = FirstBinding(documents).Value<string>("target")!;
        JObject physics = documents.Physics.OfType<JObject>()
            .Single(operation => operation.Value<string>("file") == target);
        physics.Remove();
        AssertInvalid(documents, "complete optics/physics");

        documents = ReadProductionDocuments();
        FirstOptics(documents)["profile"] = string.Empty;
        AssertInvalid(documents, "invalid physical profile");

        documents = ReadProductionDocuments();
        FirstBinding(documents)["codeRoots"] = new JArray();
        AssertInvalid(documents, "no collectible-code roots");

        documents = ReadProductionDocuments();
        FirstBinding(documents)["codeRoots"] = new JArray("invalid-root");
        AssertInvalid(documents, "invalid code root");

        documents = ReadProductionDocuments();
        JArray bindings = (JArray)documents.Bindings["bindings"]!;
        ((JObject)bindings[1]!)["codeRoots"] = ((JArray)((JObject)bindings[0]!)["codeRoots"]!).DeepClone();
        AssertInvalid(documents, "bound more than once");
    }

    /// <summary>Rejects incomplete container roots and duplicate wrapped patch targets.</summary>
    [TestMethod]
    public void ParseRejectsContainerAndPatchTopologyFailures()
    {
        PatchDocuments documents = ReadProductionDocuments();
        FirstContainerBinding(documents)["target"] = string.Empty;
        AssertInvalid(documents, "no server patch contract");

        documents = ReadProductionDocuments();
        FirstContainerBinding(documents)["target"] = "game:missing-container.json";
        AssertInvalid(documents, "no server patch contract");

        documents = ReadProductionDocuments();
        JObject containerPatch = documents.Optics.OfType<JObject>()
            .Single(operation => operation.Value<string>("file") ==
                FirstContainerBinding(documents).Value<string>("target"));
        ((JObject)containerPatch["value"]!["vintageRtxLiquidContainer"]!)["capacityLitres"] = 0;
        AssertInvalid(documents, "invalid metadata");

        documents = ReadProductionDocuments();
        FirstContainerBinding(documents)["codeRoots"] = new JArray();
        AssertInvalid(documents, "no collectible-code roots");

        documents = ReadProductionDocuments();
        FirstContainerBinding(documents)["codeRoots"] = new JArray("invalid-root");
        AssertInvalid(documents, "invalid code root");

        documents = ReadProductionDocuments();
        JArray containers = (JArray)documents.Bindings["containerBindings"]!;
        ((JObject)containers[1]!)["codeRoots"] = ((JArray)((JObject)containers[0]!)["codeRoots"]!).DeepClone();
        AssertInvalid(documents, "bound more than once");

        documents = ReadProductionDocuments();
        documents.Optics.Add(documents.Optics[0]!.DeepClone());
        AssertInvalid(documents, "defines 'vintageRtxOptics' more than once");
    }

    /// <summary>Ignores unrelated patch operations while applying one matching direct correction.</summary>
    [TestMethod]
    public void ParseSkipsNoiseAndAppliesMatchingDirectOverride()
    {
        PatchDocuments documents = ReadProductionDocuments();
        documents.Optics.Insert(0, new JObject { ["file"] = string.Empty });
        documents.Optics.Insert(1, new JObject
        {
            ["file"] = "game:ignored.json",
            ["value"] = new JObject(),
        });
        documents.Measured.Insert(0, new JObject
        {
            ["file"] = "game:blocktypes/liquid/water.json",
            ["path"] = "/wrong/path",
            ["value"] = new JObject { ["ior"] = 2.0 },
        });
        documents.Measured.Insert(1, new JObject
        {
            ["file"] = "game:blocktypes/liquid/water.json",
            ["path"] = "/attributes/vintageRtxOptics",
            ["value"] = 1,
        });
        documents.Measured.Insert(2, new JObject
        {
            ["file"] = "game:missing.json",
            ["path"] = "/attributes/vintageRtxOptics",
            ["value"] = new JObject { ["ior"] = 2.0 },
        });

        LiquidClientFallbackData result = Parse(documents);

        Assert.IsTrue(LiquidOpticalFallbackCatalog.TryResolve(
            new AssetLocation("game:water"),
            result.ProfilesByCodeRoot,
            out LiquidOpticalProfile water));
        Assert.AreEqual(0.3594f, water.Absorption.Red, 0.000001f);
        Assert.ThrowsException<ArgumentNullException>(() =>
            LiquidOpticalFallbackCatalog.TryResolve(
                new AssetLocation("game:water"),
                null!,
                out _));
    }

    /// <summary>Rejects each null byte array at the public deterministic parse boundary.</summary>
    [TestMethod]
    public void ParseRejectsNullByteArrays()
    {
        PatchInputs inputs = ReadProductionInputs();
        Assert.ThrowsException<ArgumentNullException>(() =>
            LiquidOpticalFallbackCatalog.Parse(null!, inputs.Physics, inputs.Measured, inputs.Bindings));
        Assert.ThrowsException<ArgumentNullException>(() =>
            LiquidOpticalFallbackCatalog.Parse(inputs.Optics, null!, inputs.Measured, inputs.Bindings));
        Assert.ThrowsException<ArgumentNullException>(() =>
            LiquidOpticalFallbackCatalog.Parse(inputs.Optics, inputs.Physics, null!, inputs.Bindings));
        Assert.ThrowsException<ArgumentNullException>(() =>
            LiquidOpticalFallbackCatalog.Parse(inputs.Optics, inputs.Physics, inputs.Measured, null!));
    }

    /// <summary>Returns the first liquid binding object from a mutable production clone.</summary>
    /// <param name="documents">Mutable patch documents.</param>
    /// <returns>First liquid binding.</returns>
    private static JObject FirstBinding(PatchDocuments documents) =>
        (JObject)((JArray)documents.Bindings["bindings"]!)[0]!;

    /// <summary>Returns the first container binding object from a mutable production clone.</summary>
    /// <param name="documents">Mutable patch documents.</param>
    /// <returns>First container binding.</returns>
    private static JObject FirstContainerBinding(PatchDocuments documents) =>
        (JObject)((JArray)documents.Bindings["containerBindings"]!)[0]!;

    /// <summary>Returns the first wrapped optical profile object.</summary>
    /// <param name="documents">Mutable patch documents.</param>
    /// <returns>First optical profile.</returns>
    private static JObject FirstOptics(PatchDocuments documents) =>
        (JObject)documents.Optics[0]!["value"]!["vintageRtxOptics"]!;

    /// <summary>Asserts that one mutated document set is rejected with an actionable message.</summary>
    /// <param name="documents">Mutated patch documents.</param>
    /// <param name="message">Expected diagnostic fragment.</param>
    private static void AssertInvalid(PatchDocuments documents, string message)
    {
        InvalidDataException failure = Assert.ThrowsException<InvalidDataException>(() =>
            Parse(documents));
        StringAssert.Contains(failure.Message, message);
    }

    /// <summary>Serializes mutable JSON documents and invokes the production parser.</summary>
    /// <param name="documents">Patch documents to serialize.</param>
    /// <returns>Validated client fallback data.</returns>
    private static LiquidClientFallbackData Parse(PatchDocuments documents) =>
        LiquidOpticalFallbackCatalog.Parse(
            Bytes(documents.Optics),
            Bytes(documents.Physics),
            Bytes(documents.Measured),
            Bytes(documents.Bindings));

    /// <summary>Parses independent mutable JSON clones of every production patch input.</summary>
    /// <returns>Mutable JSON documents.</returns>
    private static PatchDocuments ReadProductionDocuments()
    {
        PatchInputs inputs = ReadProductionInputs();
        return new PatchDocuments(
            JArray.Parse(Encoding.UTF8.GetString(inputs.Optics)),
            JArray.Parse(Encoding.UTF8.GetString(inputs.Physics)),
            JArray.Parse(Encoding.UTF8.GetString(inputs.Measured)),
            JObject.Parse(Encoding.UTF8.GetString(inputs.Bindings)));
    }

    /// <summary>Reads the four canonical production assets used by the client bridge.</summary>
    /// <returns>Independent byte arrays for optics, physics, corrections, and bindings.</returns>
    private static PatchInputs ReadProductionInputs()
    {
        string root = Path.Combine(
            TestPaths.FindRepositoryRoot(),
            "src",
            "VintageRTX",
            "assets",
            "vintagertx");
        return new PatchInputs(
            File.ReadAllBytes(Path.Combine(root, "patches", "liquid-optical-profiles.json")),
            File.ReadAllBytes(Path.Combine(root, "patches", "liquid-physical-properties.json")),
            File.ReadAllBytes(Path.Combine(root, "patches", "liquid-zz-measured-water-optics.json")),
            File.ReadAllBytes(Path.Combine(root, "config", "liquid-client-bindings.json")));
    }

    /// <summary>Creates a deterministic asset manager backed by canonical string locations.</summary>
    /// <param name="catalog">Assets keyed by canonical location.</param>
    /// <returns>Minimal asset-manager proxy.</returns>
    private static IAssetManager AssetManager(IReadOnlyDictionary<string, IAsset> catalog) =>
        RuntimeCoverageDispatchProxy.Create<IAssetManager>((method, arguments) =>
            method.Name == "TryGet"
                && arguments is { Length: > 0 }
                && arguments[0] is AssetLocation location
                && catalog.TryGetValue(location.ToString(), out IAsset? asset)
                    ? asset
                    : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

    /// <summary>Creates a byte-backed asset proxy.</summary>
    /// <param name="data">Immutable asset bytes.</param>
    /// <returns>Minimal asset proxy.</returns>
    private static IAsset Asset(byte[] data) =>
        RuntimeCoverageDispatchProxy.Create<IAsset>((method, _) =>
            method.Name == "get_Data"
                ? data
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

    /// <summary>Encodes one JSON token as UTF-8 bytes.</summary>
    /// <param name="token">JSON document.</param>
    /// <returns>Compact UTF-8 representation.</returns>
    private static byte[] Bytes(JToken token) => Encoding.UTF8.GetBytes(token.ToString());

    /// <summary>Immutable production patch byte arrays.</summary>
    /// <param name="Optics">Optical patch bytes.</param>
    /// <param name="Physics">Physical patch bytes.</param>
    /// <param name="Measured">Measured-water correction bytes.</param>
    /// <param name="Bindings">Client binding bytes.</param>
    private readonly record struct PatchInputs(
        byte[] Optics,
        byte[] Physics,
        byte[] Measured,
        byte[] Bindings);

    /// <summary>Mutable parsed production patch documents.</summary>
    /// <param name="Optics">Optical operations.</param>
    /// <param name="Physics">Physical operations.</param>
    /// <param name="Measured">Measured-water overrides.</param>
    /// <param name="Bindings">Client binding document.</param>
    private readonly record struct PatchDocuments(
        JArray Optics,
        JArray Physics,
        JArray Measured,
        JObject Bindings);
}
