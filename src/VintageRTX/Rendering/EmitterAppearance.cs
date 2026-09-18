using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VintageRTX.Rendering;

/// <summary>One colour/photometry definition shared by placed and entity-backed emitters.</summary>
internal readonly record struct EmitterAppearance(float Red, float Green, float Blue, EmitterPhotometry Photometry)
{
    /// <summary>Resolves actual runtime emission without creating light for an extinguished item.</summary>
    /// <param name="code">Canonical source collectible or entity code.</param>
    /// <param name="attributes">Source attributes, not the unrelated supporting block.</param>
    /// <param name="hsv">Runtime HSV emission in the game's quantized light convention.</param>
    /// <param name="catalog">Shared authored emitter catalogue.</param>
    /// <param name="appearance">Resolved colour, dimensions and candela.</param>
    /// <returns>False for absent, malformed or extinguished emission.</returns>
    internal static bool TryResolve(AssetLocation? code, JsonObject? attributes, byte[]? hsv,
        IReadOnlyDictionary<string, Newtonsoft.Json.Linq.JObject> catalog, out EmitterAppearance appearance)
    {
        appearance = default;
        if (hsv is not { Length: >= 3 } || hsv[2] == 0 || code is null) return false;
        int rgb = ColorUtil.HsvToRgb(Math.Min(hsv[0] * ColorUtil.HueMul, 255),
            Math.Min(hsv[1] * ColorUtil.SatMul, 255), Math.Min(hsv[2] * ColorUtil.BrightMul, 255));
        float red = ColorUtil.ColorR(rgb) / 255f;
        float green = ColorUtil.ColorG(rgb) / 255f;
        float blue = ColorUtil.ColorB(rgb) / 255f;
        string identity = code.ToString();
        if (!EmitterPhotometry.TryRead(attributes, hsv[2], out EmitterPhotometry photometry)
            && !EmitterPhotometryCatalog.TryResolve(code, hsv[2], catalog, out photometry))
            photometry = EmitterPhotometry.FromGameLight(identity, hsv[2]);
        if (photometry.TryGetSrgb(out float r, out float g, out float b))
        {
            red = r; green = g; blue = b;
        }
        else if (IsWarmSource(identity))
        {
            // Retain the documented legacy calibration for unprofiled fire families only.
            red = red * 0.22f + 0.78f;
            green = green * 0.22f + 0.507f;
            blue = blue * 0.22f + 0.265f;
        }
        appearance = new EmitterAppearance(red, green, blue, photometry);
        return true;
    }

    /// <summary>Recognizes legacy fire-family calibration without overriding authored chromaticity.</summary>
    private static bool IsWarmSource(string code) => new[] { "lantern", "torch", "candle", "fire", "flame", "ember", "forge", "bloomery" }
        .Any(part => code.Contains(part, StringComparison.OrdinalIgnoreCase));
}
