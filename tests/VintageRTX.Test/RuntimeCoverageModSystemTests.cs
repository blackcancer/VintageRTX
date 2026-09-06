using System.Reflection;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Configuration;
using VintageRTX.Rendering;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageRTX.Test;

/// <summary>
/// Covers the mod-system decisions that are independent of a live OpenGL
/// renderer, including client loading, command aliases and delayed game mode.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RuntimeCoverageModSystemTests
{
    /// <summary>
    /// Verifies the mod System Loads Only On Client At Expected Order regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ModSystemLoadsOnlyOnClientAtExpectedOrder()
    {
        VintageRtxModSystem system = new();

        Assert.IsTrue(system.ShouldLoad(EnumAppSide.Client));
        Assert.IsFalse(system.ShouldLoad(EnumAppSide.Server));
        Assert.AreEqual(0.15, system.ExecuteOrder(), 0.000001);
    }

    /// <summary>
    /// Verifies the asset Lifecycle Invokes Injected Discovery Services In Order regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void AssetLifecycleInvokesInjectedDiscoveryServicesInOrder()
    {
        List<string> calls = [];
        PbrSidecarAssetStore store = CreateEmptySidecarStore();
        VintageRtxModSystem system = new(
            _ => calls.Add("install"),
            _ =>
            {
                calls.Add("take");
                return store;
            },
            _ => throw new AssertFailedException("Fallback capture was not expected."));
        ICoreAPI api = RuntimeCoverageDispatchProxy.Create<ICoreAPI>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

        system.Start(api);
        system.AssetsLoaded(api);

        CollectionAssert.AreEqual(new[] { "install", "take" }, calls);
        Assert.AreSame(store, GetPrivateField<PbrSidecarAssetStore>(system, "pbrSidecarAssets"));
    }

    /// <summary>
    /// Verifies the startup Game Mode Parser Uses Sentinel For Absent Or Malformed Input regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void StartupGameModeParserUsesSentinelForAbsentOrMalformedInput()
    {
        Assert.IsFalse(VintageRtxModSystem.TryParseStartupGameMode(null, out int absent));
        Assert.AreEqual(-1, absent);
        Assert.IsFalse(VintageRtxModSystem.TryParseStartupGameMode("creative", out int malformed));
        Assert.AreEqual(-1, malformed);
        Assert.IsTrue(VintageRtxModSystem.TryParseStartupGameMode("2", out int spectator));
        Assert.AreEqual(2, spectator);
    }

    /// <summary>
    /// Verifies the debug View Parser Covers Every Documented Alias regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void DebugViewParserCoversEveryDocumentedAlias()
    {
        Dictionary<VintageRtxDebugView, string[]> aliases = new()
        {
            [VintageRtxDebugView.Final] = ["final", "off"],
            [VintageRtxDebugView.Normal] = ["normal", "normals"],
            [VintageRtxDebugView.Position] = ["position", "depth"],
            [VintageRtxDebugView.Lighting] = ["lighting", "ssgi"],
            [VintageRtxDebugView.VoxelAlbedo] = ["voxel", "albedo"],
            [VintageRtxDebugView.VoxelVisibility] = ["visibility", "shadow"],
            [VintageRtxDebugView.VoxelShadow] = ["shadowmask", "voxel-shadow"],
            [VintageRtxDebugView.VoxelReflection] = ["voxelreflection", "voxel-reflection", "offscreen"],
            [VintageRtxDebugView.Reflection] = ["reflection", "reflections", "ssr"],
            [VintageRtxDebugView.VoxelBounce] = ["bounce", "gi", "voxelbounce"],
            [VintageRtxDebugView.TransportComponents] = ["components", "transport"],
            [VintageRtxDebugView.Material] = ["material", "pbr", "roughness", "metallic"],
            [VintageRtxDebugView.Water] = ["water", "fluid", "planar"],
            [VintageRtxDebugView.Wetness] = ["wet", "wetness", "rain"],
            [VintageRtxDebugView.ReflectionSource] = ["reflectionsource", "reflection-source", "source"],
            [VintageRtxDebugView.LiquidSurfaceField] = ["surfacefield", "surface-field", "liquidfield"],
            [VintageRtxDebugView.EntityMirror] = ["entitymirror", "entity-mirror", "entities"],
            [VintageRtxDebugView.NativeSunShadow] = ["nativeshadow", "native-shadow", "cascade"]
        };

        foreach ((VintageRtxDebugView expected, string[] names) in aliases)
        {
            foreach (string name in names)
            {
                Assert.IsTrue(VintageRtxModSystem.TryResolveDebugView(name, out VintageRtxDebugView actual));
                Assert.AreEqual(expected, actual, name);
            }
        }

        Assert.IsTrue(VintageRtxModSystem.TryResolveDebugView("WATER", out VintageRtxDebugView uppercase));
        Assert.AreEqual(VintageRtxDebugView.Water, uppercase);
        Assert.IsFalse(VintageRtxModSystem.TryResolveDebugView(null, out VintageRtxDebugView absent));
        Assert.AreEqual((VintageRtxDebugView)(-1), absent);
        Assert.IsFalse(VintageRtxModSystem.TryResolveDebugView("unknown", out _));
    }

    /// <summary>
    /// Verifies the startup Registration Ignores Malformed Environment Value regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void StartupRegistrationIgnoresMalformedEnvironmentValue()
    {
        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE");
        List<string> eventCalls = [];
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE", "invalid");
            VintageRtxModSystem system = new();
            ICoreClientAPI api = CreateStartupApi(eventCalls, [], new ListenerCapture());

            InvokePrivate(system, "RegisterStartupWorldState", api);

            Assert.AreEqual(0, eventCalls.Count);
            Assert.AreEqual(-1, GetPrivateField<int>(system, "startupGameMode"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE", previous);
        }
    }

    /// <summary>
    /// Verifies that startup sends creative mode immediately, then holds the
    /// scenario barrier until synchronized player data confirms the change.
    /// </summary>
    [TestMethod]
    public void StartupRegistrationWaitsForPlayerThenSendsGamemodeOnce()
    {
        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE");
        List<string> eventCalls = [];
        List<string> chatMessages = [];
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE", "2");
            VintageRtxModSystem system = new();
            ListenerCapture capture = new();
            ICoreClientAPI api = CreateStartupApi(eventCalls, chatMessages, capture);

            InvokePrivate(system, "RegisterStartupWorldState", api);
            Action<float>? listener = capture.Listener;
            Assert.IsNotNull(listener);
            CollectionAssert.Contains(eventCalls, "RegisterGameTickListener");
            Assert.AreEqual(91L, GetPrivateField<long>(system, "startupStateListenerId"));

            // StartClientSide normally assigns this before registration. The
            // first direct callback deliberately covers the defensive null path.
            listener(0.1f);
            Assert.AreEqual(0, chatMessages.Count);
            SetPrivateField(system, "api", api);

            listener(0.1f);
            CollectionAssert.AreEqual(new[] { "/gamemode 2" }, chatMessages);
            Assert.IsTrue(GetPrivateField<bool>(system, "startupGameModeCommandSent"));
            Assert.IsFalse(GetPrivateField<bool>(system, "startupWorldStateReady"));
            Assert.IsFalse(eventCalls.Contains("UnregisterGameTickListener"));

            listener(0.1f);
            CollectionAssert.AreEqual(new[] { "/gamemode 2" }, chatMessages);
            Assert.IsFalse(GetPrivateField<bool>(system, "startupWorldStateReady"));

            capture.GameMode = EnumGameMode.Creative;
            listener(0.1f);

            Assert.IsTrue(GetPrivateField<bool>(system, "startupWorldStateReady"));
            CollectionAssert.Contains(eventCalls, "UnregisterGameTickListener");
            Assert.AreEqual(0L, GetPrivateField<long>(system, "startupStateListenerId"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE", previous);
        }
    }

    /// <summary>
    /// Verifies that an accepted startup command cannot deadlock every later runtime command when
    /// Vintage Story keeps the client-side <see cref="IWorldPlayerData.CurrentGameMode"/> stale.
    /// </summary>
    [TestMethod]
    public void StartupRegistrationUsesBoundedSettlingWindowForStaleClientMode()
    {
        string? previous = Environment.GetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE");
        List<string> eventCalls = [];
        List<string> chatMessages = [];
        try
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE", "2");
            VintageRtxModSystem system = new();
            ListenerCapture capture = new();
            ICoreClientAPI api = CreateStartupApi(eventCalls, chatMessages, capture);
            SetPrivateField(system, "api", api);

            InvokePrivate(system, "RegisterStartupWorldState", api);
            Action<float> listener = capture.Listener
                ?? throw new InvalidOperationException("Startup listener was not registered.");
            listener(0.1f);
            for (int index = 0; index < 19; index++)
            {
                listener(0.1f);
            }

            Assert.IsFalse(GetPrivateField<bool>(system, "startupWorldStateReady"));
            listener(0.1f);

            Assert.IsTrue(GetPrivateField<bool>(system, "startupWorldStateReady"));
            CollectionAssert.AreEqual(new[] { "/gamemode 2" }, chatMessages);
            CollectionAssert.Contains(eventCalls, "UnregisterGameTickListener");
            Assert.AreEqual(0L, GetPrivateField<long>(system, "startupStateListenerId"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGERTX_AUTO_GAMEMODE", previous);
        }
    }

    /// <summary>
    /// Verifies the startup World State Defensively Handles Each Missing World Object regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void StartupWorldStateDefensivelyHandlesEachMissingWorldObject()
    {
        VintageRtxModSystem system = new();
        ICoreClientAPI noWorld = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        SetPrivateField(system, "api", noWorld);
        TargetInvocationException missingWorld = Assert.ThrowsException<TargetInvocationException>(
            () => InvokePrivate(system, "ApplyStartupWorldState", 0.1f));
        Assert.IsInstanceOfType<NullReferenceException>(missingWorld.InnerException);

        IClientWorldAccessor worldWithoutPlayer = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>(
            (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI noPlayer = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_World"
                ? worldWithoutPlayer
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        SetPrivateField(system, "api", noPlayer);
        InvokePrivate(system, "ApplyStartupWorldState", 0.1f);

        IClientPlayer playerWithoutEntity = RuntimeCoverageDispatchProxy.Create<IClientPlayer>(
            (method, _) => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IClientWorldAccessor worldWithoutEntity = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>(
            (method, _) => method.Name == "get_Player"
                ? playerWithoutEntity
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI noEntity = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) =>
            method.Name == "get_World"
                ? worldWithoutEntity
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        SetPrivateField(system, "api", noEntity);
        InvokePrivate(system, "ApplyStartupWorldState", 0.1f);

        Assert.AreEqual(0, GetPrivateField<int>(system, "startupWorldReadyTicks"));
    }

    /// <summary>
    /// Verifies the dispose Clears Empty And Partially Initialized System State regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void DisposeClearsEmptyAndPartiallyInitializedSystemState()
    {
        EnsureHarmonyLoaded();
        VintageRtxModSystem empty = new();
        empty.Dispose();

        List<string> eventCalls = [];
        ICoreClientAPI api = CreateStartupApi(eventCalls, [], new ListenerCapture());
        ConfigStore configStore = CreateConfigStore();
        VintageRtxModSystem partial = new();
        SetPrivateField(partial, "api", api);
        SetPrivateField(partial, "configStore", configStore);
        SetPrivateField(partial, "startupStateListenerId", 19L);

        partial.Dispose();

        CollectionAssert.Contains(eventCalls, "UnregisterGameTickListener");
        Assert.IsNull(GetPrivateField<object?>(partial, "api"));
        Assert.IsNull(GetPrivateField<object?>(partial, "configStore"));
        Assert.AreEqual(0L, GetPrivateField<long>(partial, "startupStateListenerId"));
    }

    /// <summary>
    /// Verifies the client Bootstrap Registers And Executes Every Rendering Command regression contract against deterministic fixture data.
    /// </summary>
    [TestMethod]
    public void ClientBootstrapRegistersAndExecutesEveryRenderingCommand()
    {
        EnsureHarmonyLoaded();
        string[] isolatedVariables =
        [
            "VINTAGERTX_AUTO_GAMEMODE",
            "VINTAGERTX_TEST_SCENARIO",
            "VINTAGERTX_TEST_TIME_HOUR",
            "VINTAGERTX_TEST_CLEAR_WEATHER",
            "VINTAGERTX_TEST_PRECIPITATION",
            "VINTAGERTX_AUTO_CAPTURE",
            "VINTAGERTX_AUTO_CAPTURE_PROFILE",
            "VINTAGERTX_AUTO_BENCHMARK"
        ];
        Dictionary<string, string?> previous = isolatedVariables.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable);
        try
        {
            foreach (string key in isolatedVariables)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
            Environment.SetEnvironmentVariable("VINTAGERTX_TEST_SCENARIO", "held-light");

            Dictionary<string, OnCommandDelegate> commands = new(StringComparer.Ordinal);
            List<string> eventCalls = [];
            ICoreClientAPI api = CreateBootstrapApi(commands, eventCalls);
            InitializeLanguage();
            int fallbackCaptureCalls = 0;
            VintageRtxModSystem system = new(
                _ => { },
                _ => CreateEmptySidecarStore(),
                _ =>
                {
                    fallbackCaptureCalls++;
                    return CreateEmptySidecarStore();
                });

            system.StartClientSide(api);

            Assert.AreEqual(1, fallbackCaptureCalls);
            string[] expectedCommands =
            [
                "status", "toggle", "reload", "lighting", "voxel", "capture", "debug", "preset", "profile"
            ];
            CollectionAssert.AreEquivalent(expectedCommands, commands.Keys.ToArray());
            Assert.AreEqual(10, eventCalls.Count(static name => name == "RegisterRenderer"));
            string[] expectedRendererRegistrations =
            [
                "RegisterRenderer:vintagertx-pbr-terrain:Opaque",
                "RegisterRenderer:vintagertx-pbr-entities:Opaque",
                "RegisterRenderer:vintagertx-entity-mirror-source:Opaque",
                "RegisterRenderer:vintagertx-reflection-source:Opaque",
                "RegisterRenderer:vintagertx-display:AfterBlit",
                "RegisterRenderer:vintagertx-color-contract:AfterPostProcessing",
                "RegisterRenderer:vintagertx-native-shadow-far:ShadowFar",
                "RegisterRenderer:vintagertx-native-shadow-near:ShadowNear",
                "RegisterRenderer:vintagertx-camera-origin:Before",
                "RegisterRenderer:vintagertx-test-camera-lock:Before"
            ];
            CollectionAssert.AreEquivalent(
                expectedRendererRegistrations,
                eventCalls.Where(static name => name.StartsWith(
                    "RegisterRenderer:",
                    StringComparison.Ordinal)).ToArray());
            RuntimeScenarioProbe probe = GetPrivateField<RuntimeScenarioProbe>(
                system,
                "runtimeScenarioProbe");
            System.Func<bool> reloadShaders = GetPrivateField<System.Func<bool>>(
                probe,
                "reloadShaders");
            Assert.IsFalse(reloadShaders());

            AssertCommandSucceeded(commands["status"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["toggle"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["toggle"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["reload"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["lighting"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["lighting"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["voxel"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["voxel"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["capture"](CreateCommandArguments()));
            AssertCommandFailed(commands["capture"](CreateCommandArguments()));
            AssertCommandSucceeded(commands["debug"](CreateCommandArguments("water")));
            AssertCommandFailed(commands["debug"](CreateCommandArguments("not-a-view")));
            AssertCommandSucceeded(commands["preset"](CreateCommandArguments("neutral")));
            AssertCommandSucceeded(commands["preset"](CreateCommandArguments("cinematic")));
            AssertCommandSucceeded(commands["preset"](CreateCommandArguments("vivid")));
            AssertCommandFailed(commands["preset"](CreateCommandArguments("unknown")));
            AssertCommandSucceeded(commands["profile"](CreateCommandArguments("performance")));
            AssertCommandSucceeded(commands["profile"](CreateCommandArguments("balanced")));
            AssertCommandSucceeded(commands["profile"](CreateCommandArguments("quality")));
            AssertCommandSucceeded(commands["profile"](CreateCommandArguments("ultra")));
            AssertCommandSucceeded(commands["profile"](CreateCommandArguments("custom")));
            AssertCommandFailed(commands["profile"](CreateCommandArguments("unknown")));

            system.Dispose();

            Assert.AreEqual(10, eventCalls.Count(static name => name == "UnregisterRenderer"));
            Assert.IsNull(GetPrivateField<object?>(system, "renderer"));
            Assert.IsNull(GetPrivateField<object?>(system, "voxelScene"));
            Assert.IsNull(GetPrivateField<object?>(system, "pbrTerrainRenderer"));
            Assert.IsNull(GetPrivateField<object?>(system, "pbrEntityRenderer"));
        }
        finally
        {
            foreach ((string key, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>
    /// Creates startup Api with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="eventCalls">The event Calls input used to configure this deterministic test path.</param>
    /// <param name="chatMessages">The chat Messages input used to configure this deterministic test path.</param>
    /// <param name="capture">The capture input used to configure this deterministic test path.</param>
    /// <returns>The create Startup Api result consumed by the caller&apos;s assertion.</returns>
    private static ICoreClientAPI CreateStartupApi(
        List<string> eventCalls,
        List<string> chatMessages,
        ListenerCapture capture)
    {
        IClientEventAPI eventApi = RuntimeCoverageDispatchProxy.Create<IClientEventAPI>((method, arguments) =>
        {
            eventCalls.Add(method.Name);
            if (method.Name == "RegisterGameTickListener")
            {
                capture.Listener = (Action<float>)arguments![0]!;
                return 91L;
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        EntityPlayer playerEntity = new();
        IWorldPlayerData worldData = RuntimeCoverageDispatchProxy.Create<IWorldPlayerData>((method, _) =>
            method.Name == "get_CurrentGameMode"
                ? capture.GameMode
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IClientPlayer player = RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) =>
            method.Name switch
            {
                "get_Entity" => playerEntity,
                "get_WorldData" => worldData,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });
        IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>((method, _) =>
            method.Name == "get_Player"
                ? player
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreClientAPI api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, arguments) =>
        {
            return method.Name switch
            {
                "get_Event" => eventApi,
                "get_Logger" => logger,
                "get_World" => world,
                "SendChatMessage" => RecordChat(arguments, chatMessages),
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            };
        });
        return api;
    }

    /// <summary>
    /// Executes the record Chat step used by the deterministic runtime Coverage Mod System Tests fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <param name="messages">The messages input used to configure this deterministic test path.</param>
    /// <returns>The record Chat result consumed by the caller&apos;s assertion.</returns>
    private static object? RecordChat(object?[]? arguments, List<string> messages)
    {
        messages.Add((string)arguments![0]!);
        return null;
    }

    /// <summary>
    /// Creates config Store with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Config Store result consumed by the caller&apos;s assertion.</returns>
    private static ConfigStore CreateConfigStore()
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        ICoreAPI api = RuntimeCoverageDispatchProxy.Create<ICoreAPI>((method, _) => method.Name switch
        {
            "get_Logger" => logger,
            "LoadModConfig" => new VintageRtxConfig { SchemaVersion = VintageRtxConfig.CurrentSchemaVersion },
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        return new ConfigStore(api);
    }

    /// <summary>
    /// Creates bootstrap Api with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="commands">The commands input used to configure this deterministic test path.</param>
    /// <param name="eventCalls">The event Calls input used to configure this deterministic test path.</param>
    /// <returns>The create Bootstrap Api result consumed by the caller&apos;s assertion.</returns>
    private static ICoreClientAPI CreateBootstrapApi(
        Dictionary<string, OnCommandDelegate> commands,
        List<string> eventCalls)
    {
        string? activeSubcommand = null;
        IChatCommand? command = null;
        command = RuntimeCoverageDispatchProxy.Create<IChatCommand>((method, arguments) =>
        {
            switch (method.Name)
            {
                case "BeginSubCommand":
                    activeSubcommand = (string)arguments![0]!;
                    return command;
                case "HandleWith":
                    commands.Add(activeSubcommand!, (OnCommandDelegate)arguments![0]!);
                    return command;
                case "EndSubCommand":
                    activeSubcommand = null;
                    return command;
                default:
                    return method.ReturnType == typeof(IChatCommand)
                        ? command
                        : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
            }
        });

        IClientEventAPI eventApi = RuntimeCoverageDispatchProxy.Create<IClientEventAPI>((method, arguments) =>
        {
            eventCalls.Add(method.Name);
            if (method.Name == "RegisterRenderer"
                && arguments is { Length: >= 3 })
            {
                eventCalls.Add($"RegisterRenderer:{arguments[2]}:{arguments[1]}");
            }
            return method.Name == "RegisterGameTickListener"
                ? 41L
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        IClientWorldAccessor world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>((method, _) =>
            method.Name == "get_Collectibles"
                ? new List<CollectibleObject>()
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

        ICoreClientAPI? api = null;
        IChatCommandApi chatApi = RuntimeCoverageDispatchProxy.Create<IChatCommandApi>((method, _) => method.Name switch
        {
            "Create" => command,
            "get_Parsers" => new CommandArgumentParsers(api!),
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
        {
            "get_Event" => eventApi,
            "get_Logger" => logger,
            "get_World" => world,
            "get_ChatCommands" => chatApi,
            "LoadModConfig" => new VintageRtxConfig { SchemaVersion = VintageRtxConfig.CurrentSchemaVersion },
            "GetOrCreateDataPath" => Path.Combine(Path.GetTempPath(), "VintageRTX-Coverage"),
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
        return api;
    }

    /// <summary>
    /// Creates empty Sidecar Store with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <returns>The create Empty Sidecar Store result consumed by the caller&apos;s assertion.</returns>
    private static PbrSidecarAssetStore CreateEmptySidecarStore()
    {
        ConstructorInfo constructor = typeof(PbrSidecarAssetStore).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Dictionary<AssetLocation, IAsset>)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(PbrSidecarAssetStore).FullName, ".ctor");
        return (PbrSidecarAssetStore)constructor.Invoke([new Dictionary<AssetLocation, IAsset>()]);
    }

    /// <summary>
    /// Creates command Arguments with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    /// <returns>The create Command Arguments result consumed by the caller&apos;s assertion.</returns>
    private static TextCommandCallingArgs CreateCommandArguments(string? value = null)
    {
        List<ICommandArgumentParser> parsers = [];
        if (value is not null)
        {
            ICommandArgumentParser parser = RuntimeCoverageDispatchProxy.Create<ICommandArgumentParser>(
                (method, _) => method.Name == "GetValue"
                    ? value
                    : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
            parsers.Add(parser);
        }

        return new TextCommandCallingArgs
        {
            Parsers = parsers
        };
    }

    /// <summary>
    /// Asserts command Succeeded and throws when the regression contract is violated.
    /// </summary>
    /// <param name="result">The result input used to configure this deterministic test path.</param>
    private static void AssertCommandSucceeded(TextCommandResult result)
    {
        Assert.AreEqual(EnumCommandStatus.Success, result.Status, result.StatusMessage);
    }

    /// <summary>
    /// Asserts command Failed and throws when the regression contract is violated.
    /// </summary>
    /// <param name="result">The result input used to configure this deterministic test path.</param>
    private static void AssertCommandFailed(TextCommandResult result)
    {
        Assert.AreEqual(EnumCommandStatus.Error, result.Status, result.StatusMessage);
    }

    /// <summary>
    /// Executes the ensure Harmony Loaded step used by the deterministic runtime Coverage Mod System Tests fixture.
    /// </summary>
    private static void EnsureHarmonyLoaded()
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(
                static assembly => assembly.GetName().Name == "0Harmony"))
        {
            return;
        }

        string harmonyPath = Path.Combine(TestPaths.ResolveGameRoot(), "Lib", "0Harmony.dll");
        AssemblyLoadContext.Default.LoadFromAssemblyPath(harmonyPath);
    }

    /// <summary>
    /// Executes the initialize Language step used by the deterministic runtime Coverage Mod System Tests fixture.
    /// </summary>
    private static void InitializeLanguage()
    {
        ILogger logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));
        string assetsPath = Path.Combine(TestPaths.ResolveGameRoot(), "assets");
        typeof(GamePaths).GetProperty(
            nameof(GamePaths.AssetsPath),
            BindingFlags.Public | BindingFlags.Static)!.SetValue(null, assetsPath);
        Lang.PreLoad(logger, assetsPath, "en");
    }

    /// <summary>
    /// Invokes private through the fixture reflection boundary and propagates failures to the calling assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="methodName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    private static void InvokePrivate(object instance, string methodName, params object?[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
        method.Invoke(instance, arguments);
    }

    /// <summary>
    /// Returns private Field from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <typeparam name="T">Type participating in the generic test contract.</typeparam>
    /// <returns>The get Private Field result consumed by the caller&apos;s assertion.</returns>
    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        return (T)field.GetValue(instance)!;
    }

    /// <summary>
    /// Sets private Field on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="instance">The instance input used to configure this deterministic test path.</param>
    /// <param name="fieldName">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="value">The value input used to configure this deterministic test path.</param>
    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, value);
    }

    /// <summary>
    /// Supports listener Capture within the deterministic VintageRTX test infrastructure.
    /// </summary>
    private sealed class ListenerCapture
    {
        /// <summary>
        /// Gets or sets the listener value exposed to the deterministic fixture.
        /// </summary>
        internal Action<float>? Listener { get; set; }

        /// <summary>Gets or sets the game mode reported by synchronized player data.</summary>
        internal EnumGameMode GameMode { get; set; } = EnumGameMode.Survival;
    }
}
