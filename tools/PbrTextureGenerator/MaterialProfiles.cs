namespace VintageRTX.PbrTextureGenerator;

internal sealed record MaterialProfile(
    string Name,
    float HeightStrength,
    float FineWeight,
    float CoarseWeight,
    int BilateralRadius,
    float EdgeSigma,
    float BaseRoughness,
    float DetailRoughness,
    float DarkRoughness,
    float Metallic)
{
    public static IReadOnlyDictionary<string, MaterialProfile> All { get; } =
        new Dictionary<string, MaterialProfile>(StringComparer.OrdinalIgnoreCase)
        {
            // Albedo-derived heights are deliberately shallow. Colour contrast is not
            // geometric evidence, so authored height/normal maps remain the only source
            // allowed to produce steep relief.
            ["generic"] = new("generic", 2.40f, 0.50f, 0.35f, 1, 0.12f, 0.64f, 0.10f, 0.04f, 0.00f),
            ["stone"] = new("stone", 2.60f, 0.34f, 0.55f, 1, 0.13f, 0.86f, 0.08f, 0.04f, 0.00f),
            ["brick"] = new("brick", 3.40f, 0.34f, 0.72f, 1, 0.11f, 0.78f, 0.10f, 0.06f, 0.00f),
            ["wood"] = new("wood", 2.20f, 0.62f, 0.30f, 1, 0.10f, 0.58f, 0.09f, 0.04f, 0.00f),
            ["metal"] = new("metal", 1.40f, 0.52f, 0.20f, 1, 0.09f, 0.42f, 0.12f, 0.06f, 0.92f),
            // Forged faces need enough file-backed relief to remain readable at the terrain
            // shader's neutral 1.0 response. The shared generated-fallback slope cap
            // and applies only to fallback anvil maps; authored mod normals are never regenerated.
            ["anvil"] = new("anvil", 3.10f, 0.45f, 0.28f, 1, 0.09f, 0.48f, 0.12f, 0.08f, 0.96f),
            ["polished"] = new("polished", 0.80f, 0.35f, 0.12f, 1, 0.08f, 0.20f, 0.05f, 0.01f, 0.00f),
            ["polished-metal"] = new("polished-metal", 0.65f, 0.32f, 0.10f, 1, 0.07f, 0.16f, 0.05f, 0.02f, 0.96f),
            ["cloth"] = new("cloth", 2.10f, 0.75f, 0.16f, 0, 0.10f, 0.90f, 0.04f, 0.02f, 0.00f),
            ["glass"] = new("glass", 0.25f, 0.18f, 0.08f, 1, 0.07f, 0.08f, 0.02f, 0.02f, 0.00f)
        };

    public static MaterialProfile Resolve(string requestedProfile, string sourcePath)
    {
        if (!requestedProfile.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (All.TryGetValue(requestedProfile, out MaterialProfile? forced))
            {
                return forced;
            }

            throw new ArgumentException($"Unknown material profile '{requestedProfile}'.");
        }

        string path = sourcePath.Replace('\\', '/').ToLowerInvariant();
        bool polished = ContainsAny(
            path,
            "/polished/", "polished-", "polished_", "polishedrock", "polishedstone",
            "polishedpanel", "polished-panel", "polished_panel", "mirrorfinish", "mirror-finish");
        bool metal = ContainsAny(
            path,
            "/metal/", "metal-", "metal_", "copper", "bronze", "brass", "steel", "iron", "silver", "gold");

        // Specific structures precede their broader parent directories.
        if (ContainsAny(path, "/brick/", "brick-", "brick_", "brickwork", "clinker"))
        {
            return All["brick"];
        }

        if (ContainsAny(path, "/glass/", "glass-", "glass_", "windowpane", "leaded"))
        {
            return All["glass"];
        }

        if (ContainsAny(path, "/cloth/", "cloth-", "cloth_", "fabric", "carpet", "canvas", "linen", "wool"))
        {
            return All["cloth"];
        }

        if (ContainsAny(path, "/anvil/", "anvil-", "anvil_", "/anvil.", "/anvil.png"))
        {
            return All["anvil"];
        }

        if (metal && polished)
        {
            return All["polished-metal"];
        }

        if (polished)
        {
            return All["polished"];
        }

        if (metal)
        {
            return All["metal"];
        }

        if (ContainsAny(path, "/wood/", "wood-", "wood_", "/plank", "/log", "timber", "bamboo"))
        {
            return All["wood"];
        }

        if (ContainsAny(path, "/stone/", "stone-", "stone_", "/rock", "cobble", "granite", "andesite", "basalt", "slate"))
        {
            return All["stone"];
        }

        return All["generic"];
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(value.Contains);
}
