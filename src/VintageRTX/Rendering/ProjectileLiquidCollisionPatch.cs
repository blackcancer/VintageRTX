using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>
/// Bridges the engine's exact projectile/liquid physics callback to the free-surface solver. The
/// periodic entity observer remains useful for swimmers and wakes, but cannot reliably see a stone
/// that touches and leaves the surface between two samples.
/// </summary>
internal static class ProjectileLiquidCollisionPatch
{
    /// <summary>Harmony ownership key used for surgical teardown.</summary>
    private const string HarmonyId = "vintagertx.projectile-liquid-collision";
    /// <summary>Caches each projectile implementation's optional retained incident-motion field.</summary>
    private static readonly Dictionary<Type, FieldInfo?> IncidentMotionFields = [];
    /// <summary>Serializes access to the retained-motion reflection cache.</summary>
    private static readonly object CollisionGate = new();

    /// <summary>
    /// Current nested liquid-callback depth on one physics thread. Only the outermost postfix emits,
    /// after a modded override and any call to the patched base method have completed their response.
    /// </summary>
    [ThreadStatic]
    private static int callbackDepth;

    /// <summary>Owned Harmony instance while the client renderer is active.</summary>
    private static Harmony? harmony;
    /// <summary>Renderer-owned bounded queue accepting detached collision samples.</summary>
    private static Action<LiquidProjectileCollisionSample>? collisionSink;

    /// <summary>
    /// Patches the common callback and every already-loaded override so stock and modded projectiles
    /// are observed even when an override does not delegate to <see cref="Entity.OnCollideWithLiquid"/>.
    /// </summary>
    /// <param name="clientApi">Active client API owning the renderer-side bounded sink.</param>
    /// <param name="sink">Bounded renderer-owned collision consumer.</param>
    /// <returns>Whether at least the public base callback was patched.</returns>
    internal static bool Install(
        ICoreClientAPI clientApi,
        Action<LiquidProjectileCollisionSample> sink)
    {
        ArgumentNullException.ThrowIfNull(clientApi);
        ArgumentNullException.ThrowIfNull(sink);
        Uninstall();

        collisionSink = sink;
        harmony = new Harmony(HarmonyId);
        HashSet<MethodInfo> targets = [];
        // Entity.OnCollideWithLiquid is a compile-time API dependency of this assembly; reflection
        // cannot legitimately return null while the already-loaded Vintage Story ABI is compatible.
        MethodInfo baseCallback = typeof(Entity).GetMethod(
            nameof(Entity.OnCollideWithLiquid),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)!;
        targets.Add(baseCallback);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (!typeof(Entity).IsAssignableFrom(type))
                {
                    continue;
                }

                MethodInfo? callback = type.GetMethod(
                    nameof(Entity.OnCollideWithLiquid),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);
                if (callback is not null && !callback.IsAbstract)
                {
                    targets.Add(callback);
                }
            }
        }

        int patched = 0;
        foreach (MethodInfo target in targets)
        {
            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(
                        typeof(ProjectileLiquidCollisionPatch),
                        nameof(BeforeLiquidCollision)),
                    postfix: new HarmonyMethod(
                        typeof(ProjectileLiquidCollisionPatch),
                        nameof(AfterLiquidCollision)),
                    finalizer: new HarmonyMethod(
                        typeof(ProjectileLiquidCollisionPatch),
                        nameof(FinalizeLiquidCollision)));
                patched++;
            }
            catch (Exception exception)
            {
                clientApi.Logger.Warning(
                    "[VintageRTX] Projectile liquid callback {0}.{1} could not be patched: {2}",
                    DescribeDeclaringType(target),
                    target.Name,
                    exception.Message);
            }
        }

        return FinishInstallation(clientApi, patched);
    }

    /// <summary>Publishes installation outcome and tears down an unusable zero-target bridge.</summary>
    /// <param name="clientApi">Active client API providing the diagnostic logger.</param>
    /// <param name="patched">Number of callbacks Harmony patched successfully.</param>
    /// <returns>Whether at least one exact collision callback is active.</returns>
    internal static bool FinishInstallation(ICoreClientAPI clientApi, int patched)
    {
        if (patched <= 0)
        {
            clientApi.Logger.Warning(
                "[VintageRTX] Exact projectile/liquid collision bridge is unavailable; periodic crossing detection remains active.");
            Uninstall();
            return false;
        }

        clientApi.Logger.Notification(
            "[VintageRTX] Exact projectile/liquid collision bridge installed: callbacks={0}.",
            patched);
        return true;
    }

    /// <summary>Formats a reflected callback's declaring type, including dynamic-method fixtures.</summary>
    /// <param name="target">Candidate engine or mod callback.</param>
    /// <returns>Full declaring type name, or an explicit marker when reflection has none.</returns>
    internal static string DescribeDeclaringType(MethodInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.DeclaringType?.FullName ?? "<unknown>";
    }

    /// <summary>Copies incident motion before the engine applies drag or a ricochet response.</summary>
    /// <param name="__instance">Entity receiving the engine liquid callback.</param>
    /// <param name="__state">Detached contact state forwarded to the postfix.</param>
    private static void BeforeLiquidCollision(
        Entity __instance,
        out LiquidProjectileCollisionSample __state)
    {
        __state = default;
        if (collisionSink is null
            || __instance is not IProjectile
            || __instance.World is null)
        {
            return;
        }

        bool isOutermost = callbackDepth++ == 0;
        if (!isOutermost)
        {
            return;
        }

        Vec3d liveMotion = __instance.Pos.Motion;
        Vec3d incidentMotion = LiquidSurfaceWorldInputs.ClassifyEntity(__instance)
            == LiquidEntitySurfaceClass.ThrownStone
                ? ResolveIncidentMotion(__instance)
                : liveMotion;
        __state = LiquidSurfaceWorldInputs.SampleProjectileLiquidCollision(
            __instance,
            incidentMotion);
    }

    /// <summary>Captures the outgoing normal velocity and dispatches one deduplicated contact.</summary>
    /// <param name="__instance">Entity after the engine liquid response.</param>
    /// <param name="__state">Incident contact captured by the matching prefix.</param>
    private static void AfterLiquidCollision(
        Entity __instance,
        LiquidProjectileCollisionSample __state)
    {
        if (__instance is not IProjectile
            || __instance.World is null
            || callbackDepth <= 0)
        {
            return;
        }

        callbackDepth--;
        Action<LiquidProjectileCollisionSample>? sink = collisionSink;
        if (callbackDepth != 0 || sink is null || __state.MassKilograms <= 0.0f)
        {
            return;
        }

        Vec3d outgoingMotion = __instance.Pos.Motion;
        sink(__state with
        {
            OutgoingMotionX = SafeMotion(outgoingMotion.X),
            OutgoingMotionY = SafeMotion(outgoingMotion.Y),
            OutgoingMotionZ = SafeMotion(outgoingMotion.Z)
        });
    }

    /// <summary>Converts one engine motion component to finite single precision.</summary>
    /// <param name="value">Motion component after liquid response.</param>
    /// <returns>Finite saturated component, or zero for NaN and infinity.</returns>
    private static float SafeMotion(double value) => double.IsFinite(value)
        ? (float)Math.Clamp(value, -float.MaxValue, float.MaxValue)
        : 0.0f;

    /// <summary>
    /// Restores the thread-local nesting depth when an engine or mod callback throws before its
    /// postfix. A failed physics response is not converted into a surface impulse.
    /// </summary>
    /// <param name="__instance">Entity whose patched callback is being finalized.</param>
    /// <param name="__exception">Exception propagated by the patched callback, if any.</param>
    /// <returns>The unchanged exception so Harmony preserves engine semantics.</returns>
    private static Exception? FinalizeLiquidCollision(
        Entity __instance,
        Exception? __exception)
    {
        if (__exception is not null
            && __instance is IProjectile
            && __instance.World is not null
            && callbackDepth > 0)
        {
            callbackDepth--;
        }

        return __exception;
    }

    /// <summary>Reads the rebound vector retained by thrown-stone implementations when available.</summary>
    /// <param name="entity">Projectile instance at the collision boundary.</param>
    /// <returns>Stone rebound input, falling back to the live contact vector.</returns>
    internal static Vec3d ResolveIncidentMotion(Entity entity)
    {
        Type type = entity.GetType();
        FieldInfo? field;
        lock (CollisionGate)
        {
            if (!IncidentMotionFields.TryGetValue(type, out field))
            {
                field = AccessTools.Field(type, "motionBeforeCollide");
                IncidentMotionFields[type] = field;
            }
        }

        return field?.GetValue(entity) is Vec3d retained
            ? retained
            : entity.Pos.Motion;
    }

    /// <summary>Returns all types that survived an assembly's optional dependency failures.</summary>
    /// <param name="assembly">Loaded game or mod assembly.</param>
    /// <returns>Non-null types safe to inspect.</returns>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Removes only this bridge and releases world/delegate state.</summary>
    internal static void Uninstall()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        collisionSink = null;
        lock (CollisionGate)
        {
            IncidentMotionFields.Clear();
        }
        callbackDepth = 0;
    }
}
