using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Test;

/// <summary>
/// Covers configuration persistence and the exact Vintage Story primary
/// framebuffer contract used by the production renderer.
/// </summary>
[TestClass]
public sealed class RuntimeCoverageConfigAndGBufferTests
{
    /// <summary>
    /// Verifies the config Store Creates Migrates Clamps And Persists Missing Configuration regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ConfigStoreCreatesMigratesClampsAndPersistsMissingConfiguration()
    {
        List<VintageRtxConfig> writes = [];
        List<string> notifications = [];
        ICoreAPI api = CreateCoreApi(
            load: () => null,
            writes,
            notifications: notifications);

        ConfigStore store = new(api);

        Assert.AreEqual(VintageRtxConfig.CurrentSchemaVersion, store.Current.SchemaVersion);
        Assert.AreEqual(1, writes.Count);
        Assert.AreSame(store.Current, writes[0]);
        Assert.IsTrue(notifications.Any(static message => message.Contains("schema 13", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the config Store Loads Current Schema Without Migration And Save Clamps Values regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ConfigStoreLoadsCurrentSchemaWithoutMigrationAndSaveClampsValues()
    {
        VintageRtxConfig loaded = new()
        {
            SchemaVersion = VintageRtxConfig.CurrentSchemaVersion,
            Exposure = 1.25f,
            RayCount = 3,
            RenderProfile = VintageRtxRenderProfile.Quality
        };
        List<VintageRtxConfig> writes = [];
        List<string> notifications = [];
        ICoreAPI api = CreateCoreApi(() => loaded, writes, notifications: notifications);
        ConfigStore store = new(api);

        Assert.AreSame(loaded, store.Current);
        Assert.AreEqual(0, notifications.Count);
        Assert.AreEqual(1, writes.Count);
        Assert.AreEqual(VintageRtxRenderProfile.Quality, store.Current.RenderProfile);

        store.Current.Exposure = 50.0f;
        store.Current.RayCount = -10;
        store.Current.RenderProfile = (VintageRtxRenderProfile)int.MaxValue;
        store.Save();

        Assert.AreEqual(2.0f, store.Current.Exposure);
        Assert.AreEqual(1, store.Current.RayCount);
        Assert.AreEqual(VintageRtxRenderProfile.Custom, store.Current.RenderProfile);
        Assert.AreEqual(2, writes.Count);
        Assert.AreSame(store.Current, writes[1]);
    }

    /// <summary>
    /// Verifies the config Store Restores Defaults After Deserializer Failure regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ConfigStoreRestoresDefaultsAfterDeserializerFailure()
    {
        List<VintageRtxConfig> writes = [];
        List<string> warnings = [];
        ICoreAPI api = CreateCoreApi(
            load: static () => throw new InvalidDataException("broken-json"),
            writes,
            warnings: warnings);

        ConfigStore store = new(api);

        Assert.AreEqual(VintageRtxConfig.CurrentSchemaVersion, store.Current.SchemaVersion);
        Assert.AreEqual(1, writes.Count);
        Assert.IsTrue(warnings.Any(static message => message.Contains("broken-json", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the config Store Reload Replaces Current And Persists Reloaded Value regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ConfigStoreReloadReplacesCurrentAndPersistsReloadedValue()
    {
        Queue<VintageRtxConfig?> loads = new([
            new VintageRtxConfig { SchemaVersion = VintageRtxConfig.CurrentSchemaVersion, Exposure = 0.25f },
            new VintageRtxConfig { SchemaVersion = VintageRtxConfig.CurrentSchemaVersion, Exposure = -0.75f }
        ]);
        List<VintageRtxConfig> writes = [];
        ICoreAPI api = CreateCoreApi(() => loads.Dequeue(), writes);
        ConfigStore store = new(api);

        VintageRtxConfig reloaded = store.Reload();

        Assert.AreEqual(-0.75f, reloaded.Exposure);
        Assert.AreSame(reloaded, store.Current);
        Assert.AreEqual(2, writes.Count);
        Assert.AreSame(reloaded, writes[1]);
    }

    /// <summary>Verifies a newer configuration schema is never destructively rewritten by this build.</summary>
    [TestMethod]
    public void ConfigStoreTreatsFutureSchemaAsReadOnly()
    {
        VintageRtxConfig future = new()
        {
            SchemaVersion = VintageRtxConfig.CurrentSchemaVersion + 1,
            RenderProfile = VintageRtxRenderProfile.Ultra,
            Exposure = 0.50f
        };
        List<VintageRtxConfig> writes = [];
        List<string> warnings = [];
        ICoreAPI api = CreateCoreApi(() => future, writes, warnings: warnings);

        ConfigStore store = new(api);
        store.Current.Exposure = 0.75f;
        store.Save();

        Assert.AreSame(future, store.Current);
        Assert.AreEqual(VintageRtxConfig.CurrentSchemaVersion + 1, store.Current.SchemaVersion);
        Assert.AreEqual(0, writes.Count);
        Assert.AreEqual(2, warnings.Count);
        Assert.IsTrue(warnings.All(static message =>
            message.Contains("not overwritten", StringComparison.Ordinal)
            || message.Contains("read-only", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies the game G Buffer Rejects Missing Primary Framebuffer regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GameGBufferRejectsMissingPrimaryFramebuffer()
    {
        IRenderAPI render = CreateRenderApi([]);

        bool resolved = GameGBuffer.TryResolve(render, out GameGBuffer gBuffer, out string reason);

        Assert.IsFalse(resolved);
        Assert.AreEqual(default, gBuffer);
        Assert.AreEqual("the Primary framebuffer is not available", reason);
    }

    /// <summary>
    /// Verifies the game G Buffer Rejects Disposed Primary Framebuffer regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GameGBufferRejectsDisposedPrimaryFramebuffer()
    {
        FrameBufferRef primary = CreatePrimary([1, 2, 3, 4]);
        primary.Disposed = true;

        AssertResolutionFailure(primary, "the Primary framebuffer is disposed");
    }

    /// <summary>
    /// Verifies the game G Buffer Rejects Null Or Short Color Attachment Lists regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GameGBufferRejectsNullOrShortColorAttachmentLists()
    {
        AssertResolutionFailure(CreatePrimary(null), "the Primary framebuffer exposes only 0 color attachments");
        AssertResolutionFailure(CreatePrimary([1, 2, 3]), "the Primary framebuffer exposes only 3 color attachments");
    }

    /// <summary>
    /// Verifies the game G Buffer Rejects Every Invalid Required Texture Slot regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GameGBufferRejectsEveryInvalidRequiredTextureSlot()
    {
        AssertResolutionFailure(CreatePrimary([99, 0, 3, 4]), "the glow, normal or position attachment has no texture");
        AssertResolutionFailure(CreatePrimary([99, 2, -1, 4]), "the glow, normal or position attachment has no texture");
        AssertResolutionFailure(CreatePrimary([99, 2, 3, 0]), "the glow, normal or position attachment has no texture");
    }

    /// <summary>
    /// Verifies the game G Buffer Returns Required Primary Textures regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void GameGBufferReturnsRequiredPrimaryTextures()
    {
        FrameBufferRef primary = CreatePrimary([99, 17, 23, 42, 101]);
        IRenderAPI render = CreateRenderApi(CreateFramebufferList(primary));

        bool resolved = GameGBuffer.TryResolve(render, out GameGBuffer gBuffer, out string reason);

        Assert.IsTrue(resolved);
        Assert.AreEqual(new GameGBuffer(17, 23, 42), gBuffer);
        Assert.AreEqual(17, gBuffer.GlowTextureId);
        Assert.AreEqual(23, gBuffer.NormalTextureId);
        Assert.AreEqual(42, gBuffer.PositionTextureId);
        (int glow, int normal, int position) = gBuffer;
        CollectionAssert.AreEqual(new[] { 17, 23, 42 }, new[] { glow, normal, position });
        StringAssert.Contains(gBuffer.ToString(), "GlowTextureId = 17");
        Assert.AreEqual(new GameGBuffer(17, 23, 42).GetHashCode(), gBuffer.GetHashCode());
        Assert.AreEqual("ready", reason);
    }

    /// <summary>
    /// Creates core Api with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="load">The load input used to configure this deterministic test path.</param>
    /// <param name="writes">The writes input used to configure this deterministic test path.</param>
    /// <param name="notifications">The notifications input used to configure this deterministic test path.</param>
    /// <param name="warnings">The warnings input used to configure this deterministic test path.</param>
    /// <returns>The create Core Api result consumed by the caller&apos;s assertion.</returns>
    private static ICoreAPI CreateCoreApi(
        Func<VintageRtxConfig?> load,
        List<VintageRtxConfig> writes,
        List<string>? notifications = null,
        List<string>? warnings = null)
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Notification) or nameof(ILogger.Warning))
            {
                string formatted = FormatLog(arguments);
                (method.Name == nameof(ILogger.Notification) ? notifications : warnings)?.Add(formatted);
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

        return RuntimeCoverageDispatchProxy.Create<ICoreAPI>((method, arguments) =>
        {
            if (method.Name == "get_Logger")
            {
                return logger;
            }

            if (method.Name == "LoadModConfig")
            {
                return load();
            }

            if (method.Name == "StoreModConfig")
            {
                Assert.IsInstanceOfType<VintageRtxConfig>(arguments![0]);
                writes.Add((VintageRtxConfig)arguments[0]!);
                return null;
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
    }

    /// <summary>
    /// Executes the format Log step used by the deterministic runtime Coverage Config And G Buffer Tests fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The format Log result consumed by the caller&apos;s assertion.</returns>
    private static string FormatLog(object?[]? arguments)
    {
        Assert.IsInstanceOfType<string>(arguments![0]);
        string format = (string)arguments[0]!;
        if (arguments.Length < 2 || arguments[1] is not object[] values)
        {
            return format;
        }

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, values);
    }

    /// <summary>
    /// Creates render Api with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="frameBuffers">Caller-owned input consumed only for the duration of this test step.</param>
    /// <returns>The create Render Api result consumed by the caller&apos;s assertion.</returns>
    private static IRenderAPI CreateRenderApi(List<FrameBufferRef> frameBuffers)
    {
        return RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, _) =>
            method.Name == "get_FrameBuffers"
                ? frameBuffers
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
    }

    /// <summary>
    /// Creates primary with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="colorTextureIds">The color Texture Ids input used to configure this deterministic test path.</param>
    /// <returns>The create Primary result consumed by the caller&apos;s assertion.</returns>
    private static FrameBufferRef CreatePrimary(int[]? colorTextureIds)
    {
        return new FrameBufferRef
        {
            ColorTextureIds = colorTextureIds!
        };
    }

    /// <summary>
    /// Creates framebuffer List with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="primary">Coordinate component in the space defined by the tested API.</param>
    /// <returns>The create Framebuffer List result consumed by the caller&apos;s assertion.</returns>
    private static List<FrameBufferRef> CreateFramebufferList(FrameBufferRef primary)
    {
        int primaryIndex = (int)EnumFrameBuffer.Primary;
        List<FrameBufferRef> frameBuffers = [];
        for (int index = 0; index <= primaryIndex; index++)
        {
            frameBuffers.Add(new FrameBufferRef());
        }

        frameBuffers[primaryIndex] = primary;
        return frameBuffers;
    }

    /// <summary>
    /// Asserts resolution Failure and throws when the regression contract is violated.
    /// </summary>
    /// <param name="primary">Coordinate component in the space defined by the tested API.</param>
    /// <param name="expectedReason">Expected value enforced by the regression contract.</param>
    private static void AssertResolutionFailure(FrameBufferRef primary, string expectedReason)
    {
        IRenderAPI render = CreateRenderApi(CreateFramebufferList(primary));

        Assert.IsFalse(GameGBuffer.TryResolve(render, out GameGBuffer gBuffer, out string reason));
        Assert.AreEqual(default, gBuffer);
        Assert.AreEqual(expectedReason, reason);
    }
}
