using VintageRTX.Configuration;

namespace VintageRTX.Rendering;

/// <summary>Separates numeric shader diagnostics from the game's graded player-facing output.</summary>
internal static class FrameCaptureDiagnosticContract
{
    /// <summary>Whether an explicit, supported view requires a pre-final readback.</summary>
    /// <param name="view">Latched capture view; null preserves the ordinary manual comparison.</param>
    /// <returns>True for defined non-final views, never for unknown enum values.</returns>
    internal static bool RequiresRaw(VintageRtxDebugView? view) => view.HasValue
        && view.Value != VintageRtxDebugView.Final && Enum.IsDefined(view.Value);

    /// <summary>Identifies numeric channels; native reflection carriers keep their prior paired-image role.</summary>
    /// <param name="view">Latched capture view.</param>
    /// <returns>Whether the validator image must be the ungraded channel itself.</returns>
    internal static bool IsChannelDiagnostic(VintageRtxDebugView? view) => RequiresRaw(view)
        && view is not VintageRtxDebugView.ReflectionSource and not VintageRtxDebugView.EntityMirror;

    /// <summary>Describes the shader's existing visualization encoding; no channel is recolored here.</summary>
    /// <param name="view">Latched capture view.</param>
    /// <returns>Machine-readable provenance, not a claim of unscaled HDR transport.</returns>
    internal static string DescribeEncoding(VintageRtxDebugView? view) => view switch
    {
        VintageRtxDebugView.Normal => "signed-view-normal-mapped-to-unorm8",
        VintageRtxDebugView.Position => "one-minus-exp-minus-view-depth-times-0.025-unorm8",
        VintageRtxDebugView.VoxelBounce => "linear-bounce-times-config-strength-times-8-clamped-unorm8",
        VintageRtxDebugView.Reflection or VintageRtxDebugView.VoxelReflection
            => "shader-reflection-rgb-times-confidence-times-2.5-unorm8",
        VintageRtxDebugView.LiquidSurfaceField => "shader-signed-height-and-horizontal-normal-unorm8",
        VintageRtxDebugView.ReflectionSource or VintageRtxDebugView.EntityMirror => "native-rgb-carrier-unorm8",
        null or VintageRtxDebugView.Final => "engine-post-final-rgba8",
        _ => "shader-defined-channel-diagnostic-unorm8"
    };
}
