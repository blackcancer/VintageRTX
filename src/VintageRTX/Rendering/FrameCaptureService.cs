using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using SkiaSharp;
using VintageRTX.Configuration;
using Vintagestory.API.Client;

namespace VintageRTX.Rendering;

/// <summary>Names one comparison frame and optionally selects a temporary shader diagnostic view.</summary>
internal readonly record struct FrameCaptureRequest(
    string Label,
    VintageRtxDebugView? DebugViewOverride);

/// <summary>Identifies which official post-final frame a transaction must capture.</summary>
internal enum FrameCapturePhase
{
    /// <summary>Capture the unmodified frame after Vintage Story's official final shader.</summary>
    Baseline,
    /// <summary>Capture the VintageRTX frame after Vintage Story's official final shader.</summary>
    Effect
}

/// <summary>One latched capture request and the post-final frame currently required for it.</summary>
internal readonly record struct FrameCaptureStep(
    FrameCaptureRequest Request,
    FrameCapturePhase Phase);

/// <summary>Describes how a submitted post-final framebuffer advanced its capture transaction.</summary>
internal enum FrameCaptureAdvanceResult
{
    /// <summary>The submitted step did not match the active transaction.</summary>
    Rejected,
    /// <summary>The baseline framebuffer was retained and an effect frame is now required.</summary>
    BaselineStored,
    /// <summary>The final pair was encoded and the transaction completed.</summary>
    PairSaved,
    /// <summary>The pair was inconsistent and the same request was queued for a new baseline.</summary>
    RestartQueued,
    /// <summary>The pair was consistent but could not be written to disk.</summary>
    SaveFailed
}

/// <summary>
/// Coordinates deterministic before/after framebuffer readback for manual QA and automated render
/// scenarios. Scheduling deliberately separates scene warm-up, diagnostic capture, and benchmarking.
/// </summary>
internal sealed class FrameCaptureService
{
    /// <summary>Internal synchronization points of a two-frame post-final capture.</summary>
    private enum CaptureTransactionState
    {
        /// <summary>No transaction owns a request or framebuffer.</summary>
        Idle,
        /// <summary>A latched request is waiting to start or restart its baseline frame.</summary>
        BaselineFramePending,
        /// <summary>The baseline frame began and must submit its official post-final pixels.</summary>
        BaselineReadbackPending,
        /// <summary>A valid baseline is stored and a later effect frame must begin.</summary>
        EffectFramePending,
        /// <summary>The effect frame began and must submit its official post-final pixels.</summary>
        EffectReadbackPending
    }

    private static readonly (long Frame, string Label, VintageRtxDebugView? DebugView)[] AutomaticSequence =
    [
        (180, "final", VintageRtxDebugView.Final),
        (200, "normal", VintageRtxDebugView.Normal),
        (220, "position", VintageRtxDebugView.Position),
        (240, "lighting", VintageRtxDebugView.Lighting),
        (250, "reflection", VintageRtxDebugView.Reflection),
        (260, "voxel-reflection", VintageRtxDebugView.VoxelReflection),
        (270, "voxel-albedo", VintageRtxDebugView.VoxelAlbedo),
        (280, "voxel-bounce", VintageRtxDebugView.VoxelBounce),
        (290, "voxel-visibility", VintageRtxDebugView.VoxelVisibility),
        (300, "transport-components", VintageRtxDebugView.TransportComponents),
        (310, "voxel-shadow", VintageRtxDebugView.VoxelShadow),
        (320, "material", VintageRtxDebugView.Material),
        (330, "water", VintageRtxDebugView.Water),
        (340, "wetness", VintageRtxDebugView.Wetness),
        (350, "entity-mirror", VintageRtxDebugView.EntityMirror)
    ];

    private static readonly (long Frame, string Label, VintageRtxDebugView? DebugView)[] RenderLabSequence =
    [
        (180, "final", VintageRtxDebugView.Final),
        (200, "normal", VintageRtxDebugView.Normal),
        (215, "position", VintageRtxDebugView.Position),
        (230, "material", VintageRtxDebugView.Material),
        (240, "reflection", VintageRtxDebugView.Reflection),
        (250, "voxel-reflection", VintageRtxDebugView.VoxelReflection),
        (260, "water", VintageRtxDebugView.Water),
        (270, "voxel-shadow", VintageRtxDebugView.VoxelShadow),
        (280, "native-sun-shadow", VintageRtxDebugView.NativeSunShadow)
    ];

    /// <summary>
    /// Full copied-map evidence plus a source-separated sun-shadow carrier. This remains distinct
    /// from the default campaign so existing scenarios and their terminal timing do not change.
    /// </summary>
    private static readonly (long Frame, string Label, VintageRtxDebugView? DebugView)[]
        VegetationMapSequence =
    [
        (180, "final", VintageRtxDebugView.Final),
        (200, "normal", VintageRtxDebugView.Normal),
        (220, "position", VintageRtxDebugView.Position),
        (240, "lighting", VintageRtxDebugView.Lighting),
        (250, "reflection", VintageRtxDebugView.Reflection),
        (260, "voxel-reflection", VintageRtxDebugView.VoxelReflection),
        (270, "voxel-albedo", VintageRtxDebugView.VoxelAlbedo),
        (280, "voxel-bounce", VintageRtxDebugView.VoxelBounce),
        (290, "voxel-visibility", VintageRtxDebugView.VoxelVisibility),
        (300, "transport-components", VintageRtxDebugView.TransportComponents),
        (310, "voxel-shadow", VintageRtxDebugView.VoxelShadow),
        (315, "native-sun-shadow", VintageRtxDebugView.NativeSunShadow),
        (320, "material", VintageRtxDebugView.Material),
        (330, "water", VintageRtxDebugView.Water),
        (340, "wetness", VintageRtxDebugView.Wetness),
        (350, "entity-mirror", VintageRtxDebugView.EntityMirror)
    ];

    private static readonly (long Frame, string Label, VintageRtxDebugView? DebugView)[] LiquidLabSequence =
    [
        (180, "final", VintageRtxDebugView.Final),
        (200, "normal", VintageRtxDebugView.Normal),
        (215, "material", VintageRtxDebugView.Material),
        (230, "reflection", VintageRtxDebugView.Reflection),
        (245, "voxel-reflection", VintageRtxDebugView.VoxelReflection),
        (260, "liquid-final-a", VintageRtxDebugView.Final),
        (275, "water-motion-a", VintageRtxDebugView.Water),
        // Two seconds at the supported 60 Hz reference leaves enough phase
        // separation to prove wind waves or lava bubbles without relying on
        // a single frozen diagnostic image.
        (395, "water-motion-b", VintageRtxDebugView.Water),
        (410, "liquid-final-b", VintageRtxDebugView.Final),
        (425, "liquid-transport", VintageRtxDebugView.TransportComponents)
    ];

    /// <summary>
    /// Fixed-camera lantern sequence used to measure temporal light stability independently from
    /// the ordinary one-shot diagnostics. The three final frames and three point/sun visibility
    /// frames are separated by two reference seconds, so source-slot movement, history pumping,
    /// or non-deterministic shadow filtering cannot hide inside adjacent-frame correlation.
    /// </summary>
    private static readonly (long Frame, string Label, VintageRtxDebugView? DebugView)[]
        LightStabilitySequence =
    [
        (180, "final", VintageRtxDebugView.Final),
        (200, "normal", VintageRtxDebugView.Normal),
        (220, "position", VintageRtxDebugView.Position),
        (240, "material", VintageRtxDebugView.Material),
        (260, "lighting", VintageRtxDebugView.Lighting),
        (280, "voxel-shadow", VintageRtxDebugView.VoxelShadow),
        (400, "light-stability-final-b", VintageRtxDebugView.Final),
        (420, "light-stability-shadow-b", VintageRtxDebugView.VoxelShadow),
        (540, "light-stability-final-c", VintageRtxDebugView.Final),
        (560, "light-stability-shadow-c", VintageRtxDebugView.VoxelShadow)
    ];

    private readonly ICoreClientAPI api;
    /// <summary>Reads process configuration through a deterministic seam.</summary>
    private readonly Func<string, string?> readEnvironment;
    /// <summary>Supplies UTC wall time for capture gates and filenames.</summary>
    private readonly Func<DateTime> utcNow;
    private readonly string captureDirectory;
    private readonly (long Frame, string Label, VintageRtxDebugView? DebugView)[] automaticSequence;
    /// <summary>Stable profile name written into the durable automatic-sequence completion marker.</summary>
    private readonly string automaticCaptureProfile;
    private readonly bool renderLabProfile;
    private readonly bool liquidLabProfile;
    /// <summary>Whether the capture measures fixed-scene light and shadow stability over time.</summary>
    private readonly bool lightStabilityProfile;
    /// <summary>Whether a copied real map must wait for its server-placed plant witness.</summary>
    private readonly bool vegetationMapProfile;
    /// <summary>Whether the scenario guarantees entity witnesses for the raw mirror diagnostic.</summary>
    private readonly bool waterReflectionProfile;
    private readonly int automaticCaptureFrame;
    private readonly DateTime automaticCaptureNotBeforeUtc;
    private int uploadedVoxelGeneration;
    private bool voxelSceneSettled;
    private bool renderLabCommitObserved;
    private int renderLabCommitGeneration = -1;
    private DateTime automaticSequenceCompletedUtc = DateTime.MaxValue;
    private bool capturePending;
    private FrameCaptureRequest pendingCaptureRequest = new("manual", null);
    private int automaticCaptureIndex;
    private string status = "idle";
    private CaptureTransactionState transactionState;
    private FrameCaptureRequest transactionRequest;
    private bool transactionRequestWasAutomatic;
    private byte[]? transactionBaselinePixels;
    private int transactionBaselineWidth;
    private int transactionBaselineHeight;
    private long transactionBaselineFrame = -1;
    private long transactionLastStartedFrame = -1;
    /// <summary>Optional effect output read before Vintage Story's final shader and bloom.</summary>
    private byte[]? transactionPreFinalDiagnosticPixels;
    private int transactionPreFinalDiagnosticWidth;
    private int transactionPreFinalDiagnosticHeight;

    /// <summary>Initializes capture policy from environment variables and the mod data directory.</summary>
    /// <param name="api">Client API used for storage and capture diagnostics.</param>
    public FrameCaptureService(ICoreClientAPI api)
        : this(api, Environment.GetEnvironmentVariable, static () => DateTime.UtcNow)
    {
    }

    /// <summary>Initializes capture scheduling with test-controllable environment and clock sources.</summary>
    /// <param name="api">Client API used for storage and capture diagnostics.</param>
    /// <param name="readEnvironment">Reads one environment variable by exact name.</param>
    /// <param name="utcNow">Returns the current UTC instant.</param>
    internal FrameCaptureService(
        ICoreClientAPI api,
        Func<string, string?> readEnvironment,
        Func<DateTime> utcNow)
    {
        this.api = api;
        this.readEnvironment = readEnvironment;
        this.utcNow = utcNow;
        captureDirectory = api.GetOrCreateDataPath("VintageRTX/Captures");
        string captureProfile = readEnvironment("VINTAGERTX_AUTO_CAPTURE_PROFILE") ?? string.Empty;
        automaticCaptureProfile = string.IsNullOrWhiteSpace(captureProfile)
            ? "default"
            : captureProfile.Trim().ToLowerInvariant();
        renderLabProfile = string.Equals(
            captureProfile,
            "render-lab",
            StringComparison.OrdinalIgnoreCase);
        liquidLabProfile = string.Equals(
            captureProfile,
            "liquid-lab",
            StringComparison.OrdinalIgnoreCase);
        lightStabilityProfile = string.Equals(
            captureProfile,
            "light-stability",
            StringComparison.OrdinalIgnoreCase);
        vegetationMapProfile = string.Equals(
            captureProfile,
            "vegetation-shadow-map",
            StringComparison.OrdinalIgnoreCase);
        waterReflectionProfile = string.Equals(
            captureProfile,
            "water-reflection",
            StringComparison.OrdinalIgnoreCase);
        automaticSequence = lightStabilityProfile
            ? LightStabilitySequence
            : liquidLabProfile
                ? LiquidLabSequence
            : renderLabProfile
                ? RenderLabSequence
                : vegetationMapProfile
                    ? VegetationMapSequence
                    : waterReflectionProfile
                        ? AutomaticSequence
                        // Generic map probes do not guarantee any entity inside
                        // the mirror frustum. End on wetness rather than retrying
                        // an unavailable optional entity-only target forever.
                        : AutomaticSequence[..^1];
        // The liquid motion profile still needs physical phase separation. The
        // render lab instead uses an explicit post-commit voxel-generation gate.
        automaticCaptureNotBeforeUtc = liquidLabProfile
            ? utcNow().AddSeconds(8)
            : DateTime.MinValue;
        bool automaticCapture = string.Equals(
            readEnvironment("VINTAGERTX_AUTO_CAPTURE"),
            "1",
            StringComparison.Ordinal);
        automaticCaptureFrame = automaticCapture
            && int.TryParse(
                readEnvironment("VINTAGERTX_AUTO_CAPTURE_FRAME"),
                out int requestedCaptureFrame)
            && requestedCaptureFrame >= automaticSequence[0].Frame
                ? requestedCaptureFrame
                : automaticCapture
                    ? 360
                    : 0;
    }

    /// <summary>Gets the last user-facing queue, save, or failure state.</summary>
    public string Status => status;

    /// <summary>Gets the absolute directory receiving lossless PNG comparison pairs.</summary>
    public string CaptureDirectory => captureDirectory;

    /// <summary>
    /// Gets whether no manual or event capture is queued and no two-frame transaction is active.
    /// Runtime scenarios use this narrower gate to ensure a named baseline reached disk before
    /// mutating the scene; unlike <see cref="ReadyForBenchmark"/>, it does not wait for the full
    /// automatic diagnostic profile or its benchmark settling interval.
    /// </summary>
    internal bool IsEventCaptureIdle => !capturePending
        && transactionState == CaptureTransactionState.Idle;

    /// <summary>
    /// Indicates that automatic diagnostics finished and a two-second GPU/allocation settling
    /// window elapsed, or that no automatic capture sequence is configured.
    /// </summary>
    public bool ReadyForBenchmark => !capturePending
        && transactionState == CaptureTransactionState.Idle
        && (automaticCaptureFrame == 0
            || (automaticSequenceCompletedUtc != DateTime.MaxValue
                && utcNow() >= automaticSequenceCompletedUtc.AddSeconds(2)));

    /// <summary>
    /// Observes the generation already uploaded by the renderer and whether the CPU scene has no
    /// pending full or partial replacement. Render-lab automatic captures use this state to reject
    /// the generation that was visible when the deterministic room committed.
    /// </summary>
    /// <param name="generation">Generation currently bound to the display shader, or zero before the first upload.</param>
    /// <param name="settled">Whether the voxel scene has no build, dirty block, or upload work pending.</param>
    internal void ObserveVoxelSceneState(int generation, bool settled)
    {
        uploadedVoxelGeneration = Math.Max(generation, 0);
        voxelSceneSettled = settled;
    }

    /// <summary>Queues one manual capture without allowing an existing request to be overwritten.</summary>
    /// <returns><see langword="false"/> when a manual capture is already pending.</returns>
    public bool QueueCapture()
    {
        return QueueCapture(new FrameCaptureRequest("manual", null));
    }

    /// <summary>Queues one named diagnostic capture for a runtime event.</summary>
    /// <param name="request">Stable label and optional shader view to capture on the next rendered frame.</param>
    /// <returns><see langword="false"/> when another manual/event request already occupies the slot.</returns>
    internal bool QueueCapture(FrameCaptureRequest request)
    {
        if (capturePending || string.IsNullOrWhiteSpace(request.Label))
        {
            return false;
        }

        capturePending = true;
        pendingCaptureRequest = request;
        status = "queued";
        return true;
    }

    /// <summary>Advances manual or automated schedules after scene and wall-clock readiness gates.</summary>
    /// <param name="renderedFrameCount">Monotonic renderer frame number since service startup.</param>
    /// <param name="request">Due request, or the default value when no capture is ready.</param>
    /// <returns>Whether the caller must read and save the current comparison frame.</returns>
    public bool TryGetCapture(long renderedFrameCount, out FrameCaptureRequest request)
    {
        if (transactionState != CaptureTransactionState.Idle)
        {
            request = default;
            return false;
        }

        return TrySelectCaptureRequest(renderedFrameCount, out request, out _);
    }

    /// <summary>
    /// Begins the next deterministic baseline or effect frame. A request remains latched until its
    /// pair is saved, cancelled, or explicitly restarted, and the effect can never begin on the
    /// same renderer frame as its baseline.
    /// </summary>
    /// <param name="renderedFrameCount">Monotonic engine-rendered frame number.</param>
    /// <param name="step">Latched request and required presentation phase.</param>
    /// <returns>Whether the caller must submit a matching post-final framebuffer this frame.</returns>
    internal bool TryBeginCaptureFrame(long renderedFrameCount, out FrameCaptureStep step)
    {
        if (transactionState == CaptureTransactionState.Idle)
        {
            if (!TrySelectCaptureRequest(
                    renderedFrameCount,
                    out transactionRequest,
                    out transactionRequestWasAutomatic))
            {
                step = default;
                return false;
            }

            transactionState = CaptureTransactionState.BaselineFramePending;
        }

        if (transactionState == CaptureTransactionState.BaselineFramePending
            && renderedFrameCount > transactionLastStartedFrame)
        {
            transactionBaselineFrame = renderedFrameCount;
            transactionLastStartedFrame = renderedFrameCount;
            transactionState = CaptureTransactionState.BaselineReadbackPending;
            status = $"capturing baseline: {transactionRequest.Label}";
            step = new FrameCaptureStep(transactionRequest, FrameCapturePhase.Baseline);
            return true;
        }

        if (transactionState == CaptureTransactionState.EffectFramePending
            && renderedFrameCount > transactionBaselineFrame)
        {
            transactionLastStartedFrame = renderedFrameCount;
            transactionState = CaptureTransactionState.EffectReadbackPending;
            status = $"capturing effect: {transactionRequest.Label}";
            step = new FrameCaptureStep(transactionRequest, FrameCapturePhase.Effect);
            return true;
        }

        step = default;
        return false;
    }

    /// <summary>
    /// Accepts pixels read after Vintage Story's official final shader for the active step. Pixel
    /// ownership transfers to the service for a stored baseline; callers may release their array.
    /// Dimension or payload mismatches discard both sides and queue the same request for restart.
    /// </summary>
    /// <param name="step">Step previously returned by <see cref="TryBeginCaptureFrame"/>.</param>
    /// <param name="pixels">Bottom-up, tightly packed RGBA8 framebuffer.</param>
    /// <param name="width">Framebuffer width in pixels.</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    /// <returns>The deterministic transaction transition caused by this readback.</returns>
    internal FrameCaptureAdvanceResult SubmitPostFinalFrame(
        FrameCaptureStep step,
        byte[] pixels,
        int width,
        int height)
    {
        bool baselineSubmission = transactionState == CaptureTransactionState.BaselineReadbackPending
            && step.Phase == FrameCapturePhase.Baseline;
        bool effectSubmission = transactionState == CaptureTransactionState.EffectReadbackPending
            && step.Phase == FrameCapturePhase.Effect;
        if ((!baselineSubmission && !effectSubmission)
            || step.Request != transactionRequest)
        {
            return FrameCaptureAdvanceResult.Rejected;
        }

        if (!HasExpectedPixelPayload(pixels, width, height))
        {
            QueueTransactionRestart("invalid post-final framebuffer payload");
            return FrameCaptureAdvanceResult.RestartQueued;
        }

        if (baselineSubmission)
        {
            transactionBaselinePixels = pixels;
            transactionBaselineWidth = width;
            transactionBaselineHeight = height;
            transactionState = CaptureTransactionState.EffectFramePending;
            status = $"baseline stored: {transactionRequest.Label}";
            return FrameCaptureAdvanceResult.BaselineStored;
        }

        if (transactionBaselinePixels is null
            || transactionBaselineWidth != width
            || transactionBaselineHeight != height)
        {
            QueueTransactionRestart(
                $"post-final dimensions changed from {transactionBaselineWidth}x{transactionBaselineHeight} "
                + $"to {width}x{height}");
            return FrameCaptureAdvanceResult.RestartQueued;
        }

        bool requiresRawDiagnostic = transactionRequest.DebugViewOverride
            is VintageRtxDebugView.ReflectionSource or VintageRtxDebugView.EntityMirror;
        if (requiresRawDiagnostic
            && transactionPreFinalDiagnosticPixels is null)
        {
            QueueTransactionRestart("raw pre-final diagnostic is unavailable");
            return FrameCaptureAdvanceResult.RestartQueued;
        }

        FrameCaptureRequest completedRequest = transactionRequest;
        byte[] completedBaseline = transactionBaselinePixels;
        byte[]? completedPreFinalDiagnostic = transactionPreFinalDiagnosticPixels;
        int completedPreFinalDiagnosticWidth = transactionPreFinalDiagnosticWidth;
        int completedPreFinalDiagnosticHeight = transactionPreFinalDiagnosticHeight;
        ClearTransaction();
        return TrySavePair(
            completedRequest,
            completedBaseline,
            pixels,
            width,
            height,
            completedPreFinalDiagnostic,
            completedPreFinalDiagnosticWidth,
            completedPreFinalDiagnosticHeight)
                ? FrameCaptureAdvanceResult.PairSaved
                : FrameCaptureAdvanceResult.SaveFailed;
    }

    /// <summary>
    /// Stores the effect pass exactly as produced before Vintage Story's final shader. This is used
    /// by reflection-source and entity-mirror diagnostics so later first-person or bloom composition
    /// cannot be mistaken for reflected geometry.
    /// </summary>
    /// <param name="step">Active effect step returned by <see cref="TryBeginCaptureFrame"/>.</param>
    /// <param name="pixels">Bottom-up, tightly packed RGBA8 pre-final framebuffer.</param>
    /// <param name="width">Framebuffer width in pixels.</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    /// <returns>Whether the pixels were accepted by the active transaction.</returns>
    internal bool SubmitPreFinalDiagnostic(
        FrameCaptureStep step,
        byte[] pixels,
        int width,
        int height)
    {
        bool expectedSubmission = transactionState == CaptureTransactionState.EffectReadbackPending
            && step.Phase == FrameCapturePhase.Effect
            && step.Request == transactionRequest
            && step.Request.DebugViewOverride
                is VintageRtxDebugView.ReflectionSource or VintageRtxDebugView.EntityMirror;
        if (!expectedSubmission)
        {
            return false;
        }

        if (!HasExpectedPixelPayload(pixels, width, height))
        {
            QueueTransactionRestart("invalid raw pre-final diagnostic payload");
            return false;
        }

        transactionPreFinalDiagnosticPixels = pixels;
        transactionPreFinalDiagnosticWidth = width;
        transactionPreFinalDiagnosticHeight = height;
        status = $"raw pre-final stored: {transactionRequest.Label}";
        return true;
    }

    /// <summary>Discards stored pixels and schedules the same latched request for a new baseline.</summary>
    /// <returns>Whether an active transaction was restarted.</returns>
    internal bool RestartCaptureTransaction()
    {
        if (transactionState == CaptureTransactionState.Idle)
        {
            return false;
        }

        QueueTransactionRestart("restart requested by caller");
        return true;
    }

    /// <summary>
    /// Cancels the active transaction. Automatic requests return to their schedule slot; manual
    /// requests are discarded because their caller explicitly owns retry policy.
    /// </summary>
    /// <returns>Whether an active transaction was cancelled.</returns>
    internal bool CancelCaptureTransaction()
    {
        if (transactionState == CaptureTransactionState.Idle)
        {
            return false;
        }

        if (transactionRequestWasAutomatic && automaticCaptureIndex > 0)
        {
            automaticCaptureIndex--;
        }

        ClearTransaction();
        status = "cancelled";
        return true;
    }

    /// <summary>Selects one pending manual request or due automatic schedule entry.</summary>
    /// <param name="renderedFrameCount">Monotonic renderer frame number since service startup.</param>
    /// <param name="request">Selected request, or the default value when none is ready.</param>
    /// <param name="automatic">Whether the selected request advances the automatic schedule.</param>
    /// <returns>Whether a request was selected.</returns>
    private bool TrySelectCaptureRequest(
        long renderedFrameCount,
        out FrameCaptureRequest request,
        out bool automatic)
    {
        if (capturePending)
        {
            capturePending = false;
            request = pendingCaptureRequest;
            pendingCaptureRequest = new FrameCaptureRequest("manual", null);
            automatic = false;
            return true;
        }

        if (renderLabProfile || liquidLabProfile || vegetationMapProfile)
        {
            string readyVariable = vegetationMapProfile
                ? "VINTAGERTX_VEGETATION_MAP_READY"
                : "VINTAGERTX_RENDER_LAB_READY";
            string? readyState = readEnvironment(readyVariable);
            bool ready = string.Equals(
                readyState,
                "1",
                StringComparison.Ordinal);
            if (!ready)
            {
                if (vegetationMapProfile
                    && string.Equals(readyState, "staging", StringComparison.OrdinalIgnoreCase))
                {
                    // The client exposes a short staging phase before sending the authoritative
                    // placement command. Latch that exact pre-mutation generation once, so a fast
                    // chunk rebuild cannot be mistaken for the floor when READY arrives later.
                    if (!renderLabCommitObserved)
                    {
                        renderLabCommitObserved = true;
                        renderLabCommitGeneration = uploadedVoxelGeneration;
                    }
                }
                else if (renderLabProfile)
                {
                    renderLabCommitObserved = false;
                    renderLabCommitGeneration = -1;
                }
                request = default;
                automatic = false;
                return false;
            }

            if ((renderLabProfile || vegetationMapProfile) && !renderLabCommitObserved)
            {
                // A staged-scene probe raises READY only after its block mutation and camera lock.
                // The currently uploaded generation can still be the scan that began before that
                // mutation, so it is the rejected floor for both lab and copied-map witnesses.
                renderLabCommitObserved = true;
                renderLabCommitGeneration = uploadedVoxelGeneration;
                request = default;
                automatic = false;
                return false;
            }

            if ((renderLabProfile || vegetationMapProfile)
                && (!voxelSceneSettled
                    || uploadedVoxelGeneration <= renderLabCommitGeneration))
            {
                request = default;
                automatic = false;
                return false;
            }
        }

        bool deterministicEnvironmentPending = string.Equals(
            readEnvironment("VINTAGERTX_TEST_ENVIRONMENT_READY"),
            "0",
            StringComparison.Ordinal);
        if (automaticCaptureFrame > 0
            && automaticCaptureIndex < automaticSequence.Length
            && !deterministicEnvironmentPending
            && utcNow() >= automaticCaptureNotBeforeUtc)
        {
            (long sequenceFrame, string label, VintageRtxDebugView? debugView) =
                automaticSequence[automaticCaptureIndex];
            long targetFrame = automaticCaptureFrame + sequenceFrame - automaticSequence[0].Frame;
            if (renderedFrameCount >= targetFrame)
            {
                automaticCaptureIndex++;
                request = new FrameCaptureRequest(label, debugView);
                automatic = true;
                return true;
            }
        }

        request = default;
        automatic = false;
        return false;
    }

    /// <summary>Checks dimensions and exact tightly packed RGBA8 byte count without overflow.</summary>
    /// <param name="pixels">Framebuffer payload to validate.</param>
    /// <param name="width">Declared framebuffer width.</param>
    /// <param name="height">Declared framebuffer height.</param>
    /// <returns>Whether the payload exactly matches its declared dimensions.</returns>
    private static bool HasExpectedPixelPayload(byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        try
        {
            return pixels.Length == checked(width * height * 4);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Discards partial evidence while retaining the active request for another baseline.</summary>
    /// <param name="reason">Actionable cause included in the service status.</param>
    private void QueueTransactionRestart(string reason)
    {
        transactionBaselinePixels = null;
        transactionBaselineWidth = 0;
        transactionBaselineHeight = 0;
        transactionBaselineFrame = -1;
        transactionPreFinalDiagnosticPixels = null;
        transactionPreFinalDiagnosticWidth = 0;
        transactionPreFinalDiagnosticHeight = 0;
        transactionState = CaptureTransactionState.BaselineFramePending;
        status = $"retry queued: {transactionRequest.Label} ({reason})";
    }

    /// <summary>Releases every request and framebuffer owned by the current transaction.</summary>
    private void ClearTransaction()
    {
        transactionState = CaptureTransactionState.Idle;
        transactionRequest = default;
        transactionRequestWasAutomatic = false;
        transactionBaselinePixels = null;
        transactionBaselineWidth = 0;
        transactionBaselineHeight = 0;
        transactionBaselineFrame = -1;
        transactionLastStartedFrame = -1;
        transactionPreFinalDiagnosticPixels = null;
        transactionPreFinalDiagnosticWidth = 0;
        transactionPreFinalDiagnosticHeight = 0;
    }

    /// <summary>Reads the bound OpenGL framebuffer as bottom-up, tightly packed RGBA8 pixels.</summary>
    /// <param name="width">Framebuffer width in pixels.</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    /// <returns>A pinned-only-during-read byte array of exactly width times height times four bytes.</returns>
    public static byte[] ReadCurrentFrame(int width, int height)
    {
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        GCHandle pinnedPixels = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            GL.ReadPixels(
                0,
                0,
                width,
                height,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pinnedPixels.AddrOfPinnedObject());
        }
        finally
        {
            pinnedPixels.Free();
        }

        return pixels;
    }

    /// <summary>
    /// Encodes the unmodified and VintageRTX frames with a shared timestamp; failures are logged and
    /// retained in <see cref="Status"/> rather than escaping into the render loop.
    /// </summary>
    /// <param name="request">Label and debug mode associated with the pair.</param>
    /// <param name="beforePixels">Bottom-up RGBA8 source framebuffer.</param>
    /// <param name="afterPixels">Bottom-up RGBA8 post-processed framebuffer.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public void SavePair(
        FrameCaptureRequest request,
        byte[] beforePixels,
        byte[] afterPixels,
        int width,
        int height)
    {
        _ = TrySavePair(request, beforePixels, afterPixels, width, height);
    }

    /// <summary>Encodes one validated pair and optional raw diagnostic, containing filesystem failures.</summary>
    /// <param name="request">Label and debug mode associated with the pair.</param>
    /// <param name="beforePixels">Bottom-up RGBA8 baseline framebuffer.</param>
    /// <param name="afterPixels">Bottom-up RGBA8 VintageRTX framebuffer.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="preFinalDiagnosticPixels">Optional pre-final RGBA8 diagnostic sharing the pair timestamp.</param>
    /// <param name="preFinalDiagnosticWidth">Raw diagnostic width, which may differ from the final framebuffer.</param>
    /// <param name="preFinalDiagnosticHeight">Raw diagnostic height, which may differ from the final framebuffer.</param>
    /// <returns>Whether every requested PNG was written successfully.</returns>
    private bool TrySavePair(
        FrameCaptureRequest request,
        byte[] beforePixels,
        byte[] afterPixels,
        int width,
        int height,
        byte[]? preFinalDiagnosticPixels = null,
        int preFinalDiagnosticWidth = 0,
        int preFinalDiagnosticHeight = 0)
    {
        string timestamp = utcNow().ToLocalTime().ToString("yyyyMMdd-HHmmssfff");
        string beforePath = Path.Combine(captureDirectory, $"{timestamp}-{request.Label}-before.png");
        string afterPath = Path.Combine(captureDirectory, $"{timestamp}-{request.Label}-vintagertx.png");
        string? rawPath = preFinalDiagnosticPixels is null
            ? null
            : Path.Combine(captureDirectory, $"{timestamp}-{request.Label}-raw.png");

        try
        {
            SavePng(beforePath, beforePixels, width, height);
            SavePng(afterPath, afterPixels, width, height);
            if (rawPath is not null)
            {
                SavePng(
                    rawPath,
                    preFinalDiagnosticPixels!,
                    preFinalDiagnosticWidth,
                    preFinalDiagnosticHeight);
            }
            status = $"saved: {afterPath}";
            if (rawPath is null)
            {
                api.Logger.Notification(
                    "[VintageRTX] Comparison capture saved: {0} and {1}",
                    beforePath,
                    afterPath);
            }
            else
            {
                api.Logger.Notification(
                    "[VintageRTX] Comparison capture saved: {0}, {1}, and raw pre-final {2}",
                    beforePath,
                    afterPath,
                    rawPath);
            }
            CompleteAutomaticSequenceIfTerminal(request);
            return true;
        }
        catch (Exception exception)
        {
            status = $"failed: {exception.Message}";
            api.Logger.Error("[VintageRTX] Comparison capture failed: {0}", exception);
            return false;
        }
    }

    /// <summary>
    /// Publishes one durable completion marker only after the terminal automatic pair and its
    /// optional raw diagnostic have reached disk. Merely selecting the last schedule entry is not
    /// completion: the runtime harness must continue until every diagnostic is actually saved.
    /// </summary>
    /// <param name="request">Successfully persisted comparison request.</param>
    private void CompleteAutomaticSequenceIfTerminal(FrameCaptureRequest request)
    {
        if (automaticCaptureFrame <= 0
            || automaticSequenceCompletedUtc != DateTime.MaxValue
            || automaticCaptureIndex < automaticSequence.Length)
        {
            return;
        }

        (long _, string terminalLabel, VintageRtxDebugView? terminalView) =
            automaticSequence[^1];
        if (request != new FrameCaptureRequest(terminalLabel, terminalView))
        {
            return;
        }

        api.Logger.Notification(
            "[VintageRTX] Automatic capture sequence completed: profile={0}, last={1}, captures={2}.",
            automaticCaptureProfile,
            terminalLabel,
            automaticSequence.Length);
        // Readback and PNG encoding are intentionally excluded from the
        // performance window. Let allocations and the GPU queue settle only
        // after the durable marker for the last diagnostic has been emitted.
        automaticSequenceCompletedUtc = utcNow();
    }

    /// <summary>Vertically flips OpenGL row order and writes an opaque lossless RGBA PNG.</summary>
    /// <param name="path">Absolute output filename.</param>
    /// <param name="bottomUpPixels">Tightly packed OpenGL RGBA8 readback.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    private static void SavePng(string path, byte[] bottomUpPixels, int width, int height)
    {
        int stride = checked(width * 4);
        byte[] topDownPixels = GC.AllocateUninitializedArray<byte>(bottomUpPixels.Length);

        for (int sourceRow = 0; sourceRow < height; sourceRow++)
        {
            int destinationRow = height - sourceRow - 1;
            System.Buffer.BlockCopy(
                bottomUpPixels,
                sourceRow * stride,
                topDownPixels,
                destinationRow * stride,
                stride);
        }

        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        Marshal.Copy(topDownPixels, 0, bitmap.GetPixels(), topDownPixels.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream output = File.Create(path);
        encoded.SaveTo(output);
    }
}
