using Vintagestory.API.Common.Entities;

namespace Vintagestory.GameContent;

/// <summary>
/// Minimal test-only facsimile of the official 1.22 player renderer contract discovered by Harmony.
/// Its exact full name lets integration tests exercise installation without loading VintagestoryLib.
/// </summary>
internal sealed class EntityPlayerShapeRenderer
{
    /// <summary>Render modes discovered by the production patch.</summary>
    internal enum TestRenderMode
    {
        /// <summary>Camera-space overlay mode.</summary>
        FirstPerson,
        /// <summary>World-space body mode.</summary>
        ThirdPerson
    }

    /// <summary>Entity identity inherited by the real renderer.</summary>
    internal Entity entity;
    /// <summary>Official player render-mode selector.</summary>
    internal TestRenderMode renderMode = TestRenderMode.FirstPerson;
    /// <summary>Held-item overlay selector.</summary>
    internal bool DoRenderHeldItem = true;
    /// <summary>Opaque mesh selected for the current draw.</summary>
    internal object? meshRefOpaque;
    /// <summary>Complete body mesh used by third-person rendering.</summary>
    internal object? thirdPersonMeshRef = new object();
    /// <summary>Animated model matrix captured by patch telemetry.</summary>
    internal float[] ModelMat = new float[16];
    /// <summary>Spectator visibility state.</summary>
    internal bool isSpectator = false;
    /// <summary>First-person projection cache.</summary>
    internal object? pMatrixHandFov = new object();
    /// <summary>Ordinary world projection cache.</summary>
    internal object? pMatrixNormalFov = new object();

    /// <summary>Creates a synthetic renderer for the supplied player.</summary>
    /// <param name="entity">Player entity rendered by the fixture.</param>
    internal EntityPlayerShapeRenderer(Entity entity)
    {
        this.entity = entity;
        meshRefOpaque = new object();
    }

    /// <summary>Gets the number of non-batched original calls allowed by the prefix.</summary>
    internal int OpaqueDrawCount { get; private set; }

    /// <summary>Gets the number of batched original calls allowed by the prefix.</summary>
    internal int BatchedDrawCount { get; private set; }

    /// <summary>Models the exact official non-batched opaque entry point.</summary>
    /// <param name="dt">Current frame duration.</param>
    /// <param name="isShadowPass">Whether the target is an engine shadow pass.</param>
    public void DoRender3DOpaque(float dt, bool isShadowPass)
    {
        _ = dt;
        _ = isShadowPass;
        OpaqueDrawCount++;
    }

    /// <summary>Models the exact official batched body submission entry point.</summary>
    /// <param name="dt">Current frame duration.</param>
    /// <param name="isShadowPass">Whether the target is an engine shadow pass.</param>
    public void DoRender3DOpaqueBatched(float dt, bool isShadowPass)
    {
        _ = dt;
        _ = isShadowPass;
        BatchedDrawCount++;
    }

    /// <summary>Models the exact official world-model transform entry point.</summary>
    /// <param name="player">Player whose transform is prepared.</param>
    /// <param name="isSelf">Whether the camera-space self branch was requested.</param>
    /// <param name="deltaTime">Current frame duration.</param>
    /// <param name="isShadowPass">Whether the target is an engine shadow pass.</param>
    public void loadModelMatrixForPlayer(
        Entity player,
        bool isSelf,
        float deltaTime,
        bool isShadowPass)
    {
        _ = player;
        _ = isSelf;
        _ = deltaTime;
        _ = isShadowPass;
        ModelMat[12] = 21.0f;
        ModelMat[13] = 22.0f;
        ModelMat[14] = 23.0f;
    }
}
