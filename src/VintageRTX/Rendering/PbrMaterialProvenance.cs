namespace VintageRTX.Rendering;

/// <summary>
/// Describes whether scalar PBR material maps were generated heuristically or authored as an
/// intentional material override.
/// </summary>
internal enum PbrMaterialProvenance
{
    /// <summary>Offline fallback inferred from the albedo and a material profile.</summary>
    Generated,

    /// <summary>Intentional material data supplied by an artist or another mod.</summary>
    Authored
}

/// <summary>Versioned parsing rules for material provenance stored in PBR manifests.</summary>
internal static class PbrMaterialProvenanceContract
{
    /// <summary>Canonical JSON value for heuristic offline output.</summary>
    internal const string GeneratedName = "generated";

    /// <summary>Canonical JSON value for intentional artist or mod output.</summary>
    internal const string AuthoredName = "authored";

    /// <summary>
    /// Resolves one entry's effective provenance. Legacy manifests predate the field and are
    /// generated-fallback ledgers by contract; schema four requires an explicit entry value or
    /// root default.
    /// </summary>
    /// <param name="schemaVersion">Manifest schema version.</param>
    /// <param name="defaultProvenance">Optional root default.</param>
    /// <param name="entryProvenance">Optional per-entry override.</param>
    /// <param name="provenance">Resolved provenance on success.</param>
    /// <returns>Whether the provenance is valid for the supplied schema.</returns>
    internal static bool TryResolve(
        int schemaVersion,
        string? defaultProvenance,
        string? entryProvenance,
        out PbrMaterialProvenance provenance)
    {
        if (schemaVersion < 4)
        {
            provenance = PbrMaterialProvenance.Generated;
            return true;
        }

        string? value = string.IsNullOrWhiteSpace(entryProvenance)
            ? defaultProvenance
            : entryProvenance;
        if (string.Equals(value, GeneratedName, StringComparison.OrdinalIgnoreCase))
        {
            provenance = PbrMaterialProvenance.Generated;
            return true;
        }

        if (string.Equals(value, AuthoredName, StringComparison.OrdinalIgnoreCase))
        {
            provenance = PbrMaterialProvenance.Authored;
            return true;
        }

        provenance = default;
        return false;
    }
}
