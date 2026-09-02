using System.Globalization;
using System.Reflection;
using VintageRTX.Configuration;
using VintageRTX.Testing;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Deterministic in-memory client used to drive the real runtime scenario
/// renderer without starting Vintage Story or allocating an OpenGL context.
/// </summary>
internal sealed class RuntimeCoverageProbeHarness
{
    private readonly Dictionary<(int X, int Y, int Z, int Layer), Block> blocks = [];
    private readonly Dictionary<int, Block> blocksById = [];
    private readonly Dictionary<string, Block> blocksByCode = new(StringComparer.Ordinal);
    private readonly IClientEventAPI eventApi;
    private readonly IClientWorldAccessor world;
    private readonly IRenderAPI render;
    private readonly IInputAPI input;
    private readonly IBlockAccessor blockAccessor;
    private readonly IBulkBlockAccessor bulkAccessor;
    private readonly IClientGameCalendar calendar;
    private readonly IClientPlayer player;
    private readonly IPlayerInventoryManager inventoryManager;
    private readonly ILogger logger;
    private readonly IWorldChunk chunk;
    private IInventory? hotbar;
    private ItemSlot? offhand;
    private int activeHotbarSlot;
    private float mouseYaw;
    private float mousePitch;
    private float cameraYaw;
    private float cameraPitch;
    private float cameraRoll;

    /// <summary>
    /// Initializes a new runtime Coverage Probe Harness fixture with the dependencies required for isolated execution.
    /// </summary>
    internal RuntimeCoverageProbeHarness()
    {
        Entity = new EntityPlayer
        {
            CameraPos = new Vec3d(0.5, 81.62, 0.5)
        };
        Entity.Pos.SetPos(0, 80, 0);
        Climate = new ClimateCondition
        {
            Rainfall = 0.0f,
            RainCloudOverlay = 0.0f
        };
        CameraMatrix = new double[16];
        CameraMatrix[10] = -1.0;

        logger = RuntimeCoverageDispatchProxy.Create<ILogger>((method, arguments) =>
        {
            if (method.Name is nameof(ILogger.Notification)
                or nameof(ILogger.Warning)
                or nameof(ILogger.Error))
            {
                Logs.Add((method.Name, FormatLog(arguments)));
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

        chunk = RuntimeCoverageDispatchProxy.Create<IWorldChunk>((method, _) =>
            RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType));

        blockAccessor = RuntimeCoverageDispatchProxy.Create<IBlockAccessor>(HandleBlockAccessor);
        bulkAccessor = RuntimeCoverageDispatchProxy.Create<IBulkBlockAccessor>(HandleBulkAccessor);
        calendar = RuntimeCoverageDispatchProxy.Create<IClientGameCalendar>(HandleCalendar);

        inventoryManager = RuntimeCoverageDispatchProxy.Create<IPlayerInventoryManager>(
            (method, arguments) => method.Name switch
            {
                "GetHotbarInventory" => hotbar,
                "get_OffhandHotbarSlot" => offhand,
                "get_ActiveHotbarSlotNumber" => activeHotbarSlot,
                "set_ActiveHotbarSlotNumber" => SetActiveHotbar(arguments),
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });

        player = RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, arguments) => method.Name switch
        {
            "get_Entity" => Entity,
            "get_InventoryManager" => inventoryManager,
            "get_CameraYaw" => cameraYaw,
            "set_CameraYaw" => SetCameraYaw(arguments),
            "get_CameraPitch" => cameraPitch,
            "set_CameraPitch" => SetCameraPitch(arguments),
            "get_CameraRoll" => cameraRoll,
            "set_CameraRoll" => SetCameraRoll(arguments),
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

        world = RuntimeCoverageDispatchProxy.Create<IClientWorldAccessor>((method, arguments) =>
            method.Name switch
            {
                "get_Player" => PlayerAvailable ? player : null,
                "get_BlockAccessor" => blockAccessor,
                "get_Calendar" => CalendarAvailable ? calendar : null,
                "GetBlockAccessorBulkUpdate" => bulkAccessor,
                _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
            });

        render = RuntimeCoverageDispatchProxy.Create<IRenderAPI>((method, arguments) => method.Name switch
        {
            "get_FrameWidth" => FrameWidth,
            "get_FrameHeight" => FrameHeight,
            "get_CameraMatrixOrigin" => CameraMatrix,
            "AddPointLight" => AddPointLight(arguments),
            "RemovePointLight" => RemovePointLight(arguments),
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

        input = RuntimeCoverageDispatchProxy.Create<IInputAPI>((method, arguments) => method.Name switch
        {
            "get_MouseYaw" => mouseYaw,
            "set_MouseYaw" => SetMouseYaw(arguments),
            "get_MousePitch" => mousePitch,
            "set_MousePitch" => SetMousePitch(arguments),
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

        eventApi = RuntimeCoverageDispatchProxy.Create<IClientEventAPI>((method, arguments) =>
        {
            EventCalls.Add(method.Name);
            if (method.Name == "RegisterGameTickListener")
            {
                Tick = (Action<float>)arguments![0]!;
                return 101L;
            }

            if (method.Name == "RegisterRenderer")
            {
                RegisteredRenderer = (IRenderer)arguments![0]!;
            }

            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

        Api = RuntimeCoverageDispatchProxy.Create<ICoreClientAPI>((method, arguments) => method.Name switch
        {
            "get_Event" => eventApi,
            "get_World" => world,
            "get_Render" => render,
            "get_Input" => input,
            "get_Logger" => logger,
            "get_IsGamePaused" => IsPaused,
            "PauseGame" => SetPaused(arguments),
            "SendChatMessage" => RecordChat(arguments),
            "TriggerChatMessage" => RecordTriggeredChat(arguments),
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });

        Air = RegisterBlock("game:air", 0, EnumBlockMaterial.Air, collision: false);
        Solid = RegisterBlock("game:solid", 1, EnumBlockMaterial.Stone, collision: true);
        Water = RegisterBlock("game:water-still-7", 2, EnumBlockMaterial.Water, collision: false, liquid: true);
        RegisterBlock("game:stonebricks-granite", 10, EnumBlockMaterial.Brick, collision: true);
        RegisterBlock("game:rockpolished-granite", 11, EnumBlockMaterial.Stone, collision: true);
        RegisterBlock("game:soil-medium-normal", 12, EnumBlockMaterial.Soil, collision: true);
        RegisterBlock("game:tallgrass-tall-free", 13, EnumBlockMaterial.Plant, collision: false);
        RegisterBlock("game:lantern-large-up", 14, EnumBlockMaterial.Metal, collision: true);
        RegisterBlock("game:anvil-iron", 15, EnumBlockMaterial.Metal, collision: true);
        RegisterBlock("game:daub-blue-normal", 16, EnumBlockMaterial.Stone, collision: true);
        RegisterBlock("game:daub-orange-normal", 17, EnumBlockMaterial.Stone, collision: true);
        RegisterBlock("game:daub-green-normal", 18, EnumBlockMaterial.Stone, collision: true);
    }

    /// <summary>
    /// Gets the api value exposed to the deterministic fixture.
    /// </summary>
    internal ICoreClientAPI Api { get; }

    /// <summary>
    /// Gets the block Accessor value exposed to the deterministic fixture.
    /// </summary>
    internal IBlockAccessor BlockAccessor => blockAccessor;

    /// <summary>
    /// Gets the entity value exposed to the deterministic fixture.
    /// </summary>
    internal EntityPlayer Entity { get; }

    /// <summary>
    /// Gets the air value exposed to the deterministic fixture.
    /// </summary>
    internal Block Air { get; }

    /// <summary>
    /// Gets the solid value exposed to the deterministic fixture.
    /// </summary>
    internal Block Solid { get; }

    /// <summary>
    /// Gets the water value exposed to the deterministic fixture.
    /// </summary>
    internal Block Water { get; }

    /// <summary>
    /// Gets or sets the climate value exposed to the deterministic fixture.
    /// </summary>
    internal ClimateCondition Climate { get; set; }

    /// <summary>
    /// Gets the logs value exposed to the deterministic fixture.
    /// </summary>
    internal List<(string Level, string Message)> Logs { get; } = [];

    /// <summary>
    /// Gets the chat Messages value exposed to the deterministic fixture.
    /// </summary>
    internal List<string> ChatMessages { get; } = [];

    /// <summary>
    /// Gets the triggered Chat Messages value exposed to the deterministic fixture.
    /// </summary>
    internal List<string> TriggeredChatMessages { get; } = [];

    /// <summary>
    /// Gets the event Calls value exposed to the deterministic fixture.
    /// </summary>
    internal List<string> EventCalls { get; } = [];

    /// <summary>
    /// Gets the point Lights value exposed to the deterministic fixture.
    /// </summary>
    internal List<IPointLight> PointLights { get; } = [];

    /// <summary>
    /// Gets or sets the tick value exposed to the deterministic fixture.
    /// </summary>
    internal Action<float>? Tick { get; private set; }

    /// <summary>
    /// Gets or sets the registered Renderer value exposed to the deterministic fixture.
    /// </summary>
    internal IRenderer? RegisteredRenderer { get; private set; }

    /// <summary>
    /// Gets or sets the player Available value exposed to the deterministic fixture.
    /// </summary>
    internal bool PlayerAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the startup game-mode barrier has been confirmed.
    /// A false value must prevent every environment and camera mutation.
    /// </summary>
    internal bool StartupWorldStateReady { get; set; } = true;

    /// <summary>
    /// Gets or sets the calendar Available value exposed to the deterministic fixture.
    /// </summary>
    internal bool CalendarAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets the is Paused value exposed to the deterministic fixture.
    /// </summary>
    internal bool IsPaused { get; set; }

    /// <summary>
    /// Gets or sets the pause Calls value exposed to the deterministic fixture.
    /// </summary>
    internal int PauseCalls { get; private set; }

    /// <summary>
    /// Gets or sets the frame Width value exposed to the deterministic fixture.
    /// </summary>
    internal int FrameWidth { get; set; } = 1920;

    /// <summary>
    /// Gets or sets the frame Height value exposed to the deterministic fixture.
    /// </summary>
    internal int FrameHeight { get; set; } = 1080;

    /// <summary>
    /// Gets or sets the camera Matrix value exposed to the deterministic fixture.
    /// </summary>
    internal double[] CameraMatrix { get; set; }

    /// <summary>
    /// Gets or sets the hour Of Day value exposed to the deterministic fixture.
    /// </summary>
    internal float HourOfDay { get; set; } = 12.0f;

    /// <summary>
    /// Gets or sets the speed Of Time value exposed to the deterministic fixture.
    /// </summary>
    internal float SpeedOfTime { get; set; }

    /// <summary>
    /// Gets or sets the daylight Strength value exposed to the deterministic fixture.
    /// </summary>
    internal float DaylightStrength { get; set; }

    /// <summary>
    /// Gets or sets the hemisphere value exposed to the deterministic fixture.
    /// </summary>
    internal EnumHemisphere Hemisphere { get; set; } = EnumHemisphere.North;

    /// <summary>
    /// Gets or sets the sun Direction value exposed to the deterministic fixture.
    /// </summary>
    internal Vec3f SunDirection { get; set; } = new(0.4f, 0.7f, 0.5f);

    /// <summary>
    /// Gets or sets the map Size Y value exposed to the deterministic fixture.
    /// </summary>
    internal int MapSizeY { get; set; } = 256;

    /// <summary>
    /// Gets or sets the chunks Available value exposed to the deterministic fixture.
    /// </summary>
    internal bool ChunksAvailable { get; set; } = true;

    /// <summary>
    /// Gets or sets the chunk Available At value exposed to the deterministic fixture.
    /// </summary>
    internal System.Func<BlockPos, bool>? ChunkAvailableAt { get; set; }

    /// <summary>
    /// Gets or sets the suppress Block Writes value exposed to the deterministic fixture.
    /// </summary>
    internal bool SuppressBlockWrites { get; set; }

    /// <summary>
    /// Gets or sets the bulk Commit Count value exposed to the deterministic fixture.
    /// </summary>
    internal int BulkCommitCount { get; private set; }

    /// <summary>Gets the generated bulk-accessor double for one-call adapter coverage.</summary>
    internal IBulkBlockAccessor BulkAccessor => bulkAccessor;

    /// <summary>
    /// Gets or sets whether complete render-lab layouts write directly into
    /// the deterministic block dictionary instead of crossing DispatchProxy.
    /// </summary>
    internal bool UseFastRenderLabWriter { get; set; } = true;

    /// <summary>
    /// Gets or sets whether render-lab chunk checks use the deterministic
    /// delegate instead of crossing the generated block-accessor proxy.
    /// </summary>
    internal bool UseFastRenderLabChunkLookup { get; set; } = true;

    /// <summary>Gets or sets whether render-lab floor reads bypass DispatchProxy.</summary>
    internal bool UseFastRenderLabBlockLookup { get; set; } = true;

    /// <summary>Gets or sets whether render-lab rain height reads bypass DispatchProxy.</summary>
    internal bool UseFastRenderLabRainHeightLookup { get; set; } = true;

    /// <summary>
    /// Gets or sets the rain Height At value exposed to the deterministic fixture.
    /// </summary>
    internal System.Func<int, int, int> RainHeightAt { get; set; } = static (_, _) => 64;

    /// <summary>
    /// Gets or sets the fallback Block At value exposed to the deterministic fixture.
    /// </summary>
    internal System.Func<int, int, int, int, Block>? FallbackBlockAt { get; set; }

    /// <summary>
    /// Gets or sets the light At value exposed to the deterministic fixture.
    /// </summary>
    internal System.Func<BlockPos, EnumLightLevelType, int> LightAt { get; set; } = static (_, _) => 0;

    /// <summary>
    /// Gets or sets the reload Result value exposed to the deterministic fixture.
    /// </summary>
    internal bool ReloadResult { get; set; } = true;

    /// <summary>
    /// Gets or sets the capture Result value exposed to the deterministic fixture.
    /// </summary>
    internal bool CaptureResult { get; set; } = true;

    /// <summary>
    /// Gets or sets the reload Calls value exposed to the deterministic fixture.
    /// </summary>
    internal int ReloadCalls { get; private set; }

    /// <summary>
    /// Gets or sets the capture Calls value exposed to the deterministic fixture.
    /// </summary>
    internal int CaptureCalls { get; private set; }

    /// <summary>Gets the named diagnostic requests emitted by runtime event probes.</summary>
    internal List<(string Label, VintageRtxDebugView View)> DiagnosticCaptures { get; } = [];

    /// <summary>Gets or sets whether the named capture transaction is durably idle.</summary>
    internal bool DiagnosticCaptureIdle { get; set; } = true;

    /// <summary>Gets or sets whether the named capture slot accepts its next request.</summary>
    internal bool DiagnosticCaptureResult { get; set; } = true;

    /// <summary>
    /// Gets the mouse Yaw value exposed to the deterministic fixture.
    /// </summary>
    internal float MouseYaw => mouseYaw;

    /// <summary>
    /// Gets the mouse Pitch value exposed to the deterministic fixture.
    /// </summary>
    internal float MousePitch => mousePitch;

    /// <summary>
    /// Gets the camera Yaw value exposed to the deterministic fixture.
    /// </summary>
    internal float CameraYaw => cameraYaw;

    /// <summary>
    /// Gets the camera Pitch value exposed to the deterministic fixture.
    /// </summary>
    internal float CameraPitch => cameraPitch;

    /// <summary>
    /// Gets the camera Roll value exposed to the deterministic fixture.
    /// </summary>
    internal float CameraRoll => cameraRoll;

    /// <summary>
    /// Gets the active Hotbar Slot value exposed to the deterministic fixture.
    /// </summary>
    internal int ActiveHotbarSlot => activeHotbarSlot;

    /// <summary>
    /// Creates probe with deterministic defaults suitable for isolated assertions.
    /// </summary>
    /// <param name="scenario">The scenario input used to configure this deterministic test path.</param>
    /// <param name="hour">The hour input used to configure this deterministic test path.</param>
    /// <param name="clearWeather">The clear Weather input used to configure this deterministic test path.</param>
    /// <param name="precipitation">The precipitation input used to configure this deterministic test path.</param>
    /// <returns>The create Probe result consumed by the caller&apos;s assertion.</returns>
    internal RuntimeScenarioProbe CreateProbe(
        string scenario,
        float? hour = null,
        bool clearWeather = false,
        float? precipitation = null)
    {
        RuntimeScenarioProbe? probe = RuntimeScenarioProbe.TryStart(
            Api,
            () =>
            {
                ReloadCalls++;
                return ReloadResult;
            },
            () =>
            {
                CaptureCalls++;
                return CaptureResult;
            },
            key => key switch
            {
                "VINTAGERTX_TEST_SCENARIO" => scenario,
                "VINTAGERTX_TEST_TIME_HOUR" => hour?.ToString(CultureInfo.InvariantCulture),
                "VINTAGERTX_TEST_CLEAR_WEATHER" => clearWeather ? "1" : null,
                "VINTAGERTX_TEST_PRECIPITATION" => precipitation?.ToString(CultureInfo.InvariantCulture),
                _ => null
            },
            UseFastRenderLabWriter
                ? (id, position, layer) => StoreBlock(id, position, layer)
                : null,
            UseFastRenderLabWriter
                ? () => BulkCommitCount++
                : null,
            UseFastRenderLabChunkLookup
                ? position => ChunkAvailableAt?.Invoke(position) ?? ChunksAvailable
                : null,
            UseFastRenderLabBlockLookup
                ? (position, layer) => ResolveBlock(position.X, position.Y, position.Z, layer)
                : null,
            UseFastRenderLabRainHeightLookup
                ? position => RainHeightAt(position.X, position.Z)
                : null,
            () => StartupWorldStateReady,
            (label, view) =>
            {
                if (!DiagnosticCaptureResult)
                {
                    return false;
                }

                DiagnosticCaptures.Add((label, view));
                return true;
            },
            () => DiagnosticCaptureIdle);
        return probe ?? throw new InvalidOperationException("The requested probe was not created.");
    }

    /// <summary>
    /// Sets hotbar on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="slots">The slots input used to configure this deterministic test path.</param>
    internal void SetHotbar(params ItemSlot?[] slots)
    {
        hotbar = RuntimeCoverageDispatchProxy.Create<IInventory>((method, arguments) => method.Name switch
        {
            "get_Count" => slots.Length,
            "get_Item" => slots[(int)arguments![0]!],
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        });
    }

    /// <summary>
    /// Executes the remove Hotbar step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    internal void RemoveHotbar()
    {
        hotbar = null;
    }

    /// <summary>
    /// Sets offhand on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="slot">The slot input used to configure this deterministic test path.</param>
    internal void SetOffhand(ItemSlot? slot)
    {
        offhand = slot;
    }

    /// <summary>
    /// Executes the register Block step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="code">Stable identifier selecting the deterministic fixture case.</param>
    /// <param name="id">The id input used to configure this deterministic test path.</param>
    /// <param name="material">The material input used to configure this deterministic test path.</param>
    /// <param name="collision">The collision input used to configure this deterministic test path.</param>
    /// <param name="liquid">The liquid input used to configure this deterministic test path.</param>
    /// <returns>The register Block result consumed by the caller&apos;s assertion.</returns>
    internal Block RegisterBlock(
        string code,
        int id,
        EnumBlockMaterial material,
        bool collision,
        bool liquid = false)
    {
        Block block = new()
        {
            BlockId = id,
            Code = new AssetLocation(code),
            BlockMaterial = material,
            MatterState = liquid ? EnumMatterState.Liquid : EnumMatterState.Solid,
            LiquidCode = liquid ? code : null!,
            CollisionBoxes = collision ? [new Cuboidf(0, 0, 0, 1, 1, 1)] : null!
        };
        blocksById[id] = block;
        blocksByCode[code] = block;
        return block;
    }

    /// <summary>
    /// Executes the remove Registered Block step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="code">Stable identifier selecting the deterministic fixture case.</param>
    internal void RemoveRegisteredBlock(string code)
    {
        if (blocksByCode.Remove(code, out Block? block))
        {
            blocksById.Remove(block.Id);
        }
    }

    /// <summary>
    /// Sets block on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="block">The block input used to configure this deterministic test path.</param>
    /// <param name="layer">The layer input used to configure this deterministic test path.</param>
    internal void SetBlock(int x, int y, int z, Block block, int layer = BlockLayersAccess.MostSolid)
    {
        blocks[(x, y, z, layer)] = block;
    }

    /// <summary>
    /// Returns placed Block from deterministic fixture state for use by the caller&apos;s assertion.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="layer">The layer input used to configure this deterministic test path.</param>
    /// <returns>The get Placed Block result consumed by the caller&apos;s assertion.</returns>
    internal Block GetPlacedBlock(int x, int y, int z, int layer = BlockLayersAccess.MostSolid)
    {
        return ResolveBlock(x, y, z, layer);
    }

    /// <summary>
    /// Executes the handle Block Accessor step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="method">The method input used to configure this deterministic test path.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The handle Block Accessor result consumed by the caller&apos;s assertion.</returns>
    private object? HandleBlockAccessor(MethodInfo method, object?[]? arguments)
    {
        if (method.Name == "get_MapSizeY")
        {
            return MapSizeY;
        }

        if (method.Name == "GetBlock")
        {
            if (arguments![0] is AssetLocation location)
            {
                return blocksByCode.GetValueOrDefault(location.ToString());
            }

            if (arguments[0] is BlockPos position)
            {
                int layer = arguments.Length > 1 ? (int)arguments[1]! : BlockLayersAccess.MostSolid;
                return ResolveBlock(position.X, position.Y, position.Z, layer);
            }

            if (arguments.Length >= 3 && arguments[0] is int x)
            {
                int layer = arguments.Length > 3 ? (int)arguments[3]! : BlockLayersAccess.MostSolid;
                return ResolveBlock(x, (int)arguments[1]!, (int)arguments[2]!, layer);
            }
        }

        if (method.Name == "GetRainMapHeightAt")
        {
            if (arguments![0] is BlockPos position)
            {
                return RainHeightAt(position.X, position.Z);
            }

            return RainHeightAt((int)arguments[0]!, (int)arguments[1]!);
        }

        if (method.Name == "GetChunkAtBlockPos")
        {
            bool available = ChunkAvailableAt?.Invoke((BlockPos)arguments![0]!) ?? ChunksAvailable;
            return available ? chunk : null;
        }

        if (method.Name == "SetBlock")
        {
            int id = (int)arguments![0]!;
            BlockPos position = (BlockPos)arguments[1]!;
            StoreBlock(id, position, arguments.Length > 3 ? (int)arguments[3]! : null);
            return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        }

        if (method.Name == "GetClimateAt")
        {
            return Climate;
        }

        if (method.Name == "GetLightLevel")
        {
            return LightAt((BlockPos)arguments![0]!, (EnumLightLevelType)arguments[1]!);
        }

        return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
    }

    /// <summary>
    /// Executes the handle Bulk Accessor step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="method">The method input used to configure this deterministic test path.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The handle Bulk Accessor result consumed by the caller&apos;s assertion.</returns>
    private object? HandleBulkAccessor(MethodInfo method, object?[]? arguments)
    {
        if (method.Name == "SetBlock")
        {
            int id = (int)arguments![0]!;
            BlockPos position = (BlockPos)arguments[1]!;
            int? layer = arguments.Length > 2 && arguments[2] is int value ? value : null;
            StoreBlock(id, position, layer);
        }
        else if (method.Name == "Commit")
        {
            BulkCommitCount++;
        }

        return RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
    }

    /// <summary>
    /// Executes the handle Calendar step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="method">The method input used to configure this deterministic test path.</param>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The handle Calendar result consumed by the caller&apos;s assertion.</returns>
    private object? HandleCalendar(MethodInfo method, object?[]? arguments)
    {
        return method.Name switch
        {
            "get_HourOfDay" => HourOfDay,
            "get_SpeedOfTime" => SpeedOfTime,
            "get_DayLightStrength" => DaylightStrength,
            "get_SunPositionNormalized" => SunDirection,
            "GetDayLightStrength" => DaylightStrength,
            "GetHemisphere" => Hemisphere,
            _ => RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType)
        };
    }

    /// <summary>
    /// Executes the resolve Block step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="x">Coordinate component in the space defined by the tested API.</param>
    /// <param name="y">Coordinate component in the space defined by the tested API.</param>
    /// <param name="z">Coordinate component in the space defined by the tested API.</param>
    /// <param name="layer">The layer input used to configure this deterministic test path.</param>
    /// <returns>The resolve Block result consumed by the caller&apos;s assertion.</returns>
    private Block ResolveBlock(int x, int y, int z, int layer)
    {
        if (blocks.TryGetValue((x, y, z, layer), out Block? exact))
        {
            return exact;
        }

        if (layer == BlockLayersAccess.MostSolid
            && blocks.TryGetValue((x, y, z, BlockLayersAccess.Solid), out Block? solid))
        {
            return solid;
        }

        return FallbackBlockAt?.Invoke(x, y, z, layer) ?? Air;
    }

    /// <summary>
    /// Executes the store Block step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="id">The id input used to configure this deterministic test path.</param>
    /// <param name="position">The position input used to configure this deterministic test path.</param>
    /// <param name="requestedLayer">The requested Layer input used to configure this deterministic test path.</param>
    private void StoreBlock(int id, BlockPos position, int? requestedLayer)
    {
        if (SuppressBlockWrites)
        {
            return;
        }

        Block block = blocksById.GetValueOrDefault(id) ?? Air;
        int layer = requestedLayer
            ?? (block.MatterState == EnumMatterState.Liquid
                ? BlockLayersAccess.Fluid
                : BlockLayersAccess.Solid);
        blocks[(position.X, position.Y, position.Z, layer)] = block;
        if (layer == BlockLayersAccess.Solid)
        {
            blocks[(position.X, position.Y, position.Z, BlockLayersAccess.MostSolid)] = block;
        }
    }

    /// <summary>
    /// Sets active Hotbar on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The set Active Hotbar result consumed by the caller&apos;s assertion.</returns>
    private object? SetActiveHotbar(object?[]? arguments)
    {
        activeHotbarSlot = (int)arguments![0]!;
        return null;
    }

    /// <summary>
    /// Sets camera Yaw on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The set Camera Yaw result consumed by the caller&apos;s assertion.</returns>
    private object? SetCameraYaw(object?[]? arguments)
    {
        cameraYaw = (float)arguments![0]!;
        return null;
    }

    /// <summary>
    /// Sets camera Pitch on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The set Camera Pitch result consumed by the caller&apos;s assertion.</returns>
    private object? SetCameraPitch(object?[]? arguments)
    {
        cameraPitch = (float)arguments![0]!;
        return null;
    }

    /// <summary>
    /// Sets camera Roll on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The set Camera Roll result consumed by the caller&apos;s assertion.</returns>
    private object? SetCameraRoll(object?[]? arguments)
    {
        cameraRoll = (float)arguments![0]!;
        return null;
    }

    /// <summary>
    /// Sets mouse Yaw on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The set Mouse Yaw result consumed by the caller&apos;s assertion.</returns>
    private object? SetMouseYaw(object?[]? arguments)
    {
        mouseYaw = (float)arguments![0]!;
        return null;
    }

    /// <summary>
    /// Sets mouse Pitch on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The set Mouse Pitch result consumed by the caller&apos;s assertion.</returns>
    private object? SetMousePitch(object?[]? arguments)
    {
        mousePitch = (float)arguments![0]!;
        return null;
    }

    /// <summary>
    /// Executes the add Point Light step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The add Point Light result consumed by the caller&apos;s assertion.</returns>
    private object? AddPointLight(object?[]? arguments)
    {
        PointLights.Add((IPointLight)arguments![0]!);
        return null;
    }

    /// <summary>
    /// Executes the remove Point Light step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The remove Point Light result consumed by the caller&apos;s assertion.</returns>
    private object? RemovePointLight(object?[]? arguments)
    {
        PointLights.Remove((IPointLight)arguments![0]!);
        return null;
    }

    /// <summary>
    /// Sets paused on the test double without invoking unrelated production side effects.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The set Paused result consumed by the caller&apos;s assertion.</returns>
    private object? SetPaused(object?[]? arguments)
    {
        IsPaused = (bool)arguments![0]!;
        PauseCalls++;
        return null;
    }

    /// <summary>
    /// Executes the record Chat step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The record Chat result consumed by the caller&apos;s assertion.</returns>
    private object? RecordChat(object?[]? arguments)
    {
        ChatMessages.Add((string)arguments![0]!);
        return null;
    }

    /// <summary>
    /// Executes the record Triggered Chat step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The record Triggered Chat result consumed by the caller&apos;s assertion.</returns>
    private object? RecordTriggeredChat(object?[]? arguments)
    {
        TriggeredChatMessages.Add((string)arguments![0]!);
        return null;
    }

    /// <summary>
    /// Executes the format Log step used by the deterministic runtime Coverage Probe Harness fixture.
    /// </summary>
    /// <param name="arguments">The arguments input used to configure this deterministic test path.</param>
    /// <returns>The format Log result consumed by the caller&apos;s assertion.</returns>
    private static string FormatLog(object?[]? arguments)
    {
        string format = (string)arguments![0]!;
        return arguments.Length > 1 && arguments[1] is object[] values
            ? string.Format(CultureInfo.InvariantCulture, format, values)
            : format;
    }
}
