namespace VintageRTX.Test;

/// <summary>
/// Supports test Paths within the deterministic VintageRTX test infrastructure.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Executes the find Repository Root step used by the deterministic test Paths fixture.
    /// </summary>
    /// <returns>The find Repository Root result consumed by the caller&apos;s assertion.</returns>
    public static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageRTX.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("VintageRTX.sln could not be found above the test output directory.");
    }

    /// <summary>
    /// Executes the resolve Game Root step used by the deterministic test Paths fixture.
    /// </summary>
    /// <returns>The resolve Game Root result consumed by the caller&apos;s assertion.</returns>
    public static string ResolveGameRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("VINTAGE_STORY");
        string root = string.IsNullOrWhiteSpace(configured)
            ? @"D:\Jeux\Vintagestory"
            : Path.GetFullPath(configured);
        if (string.Equals(Path.GetFileName(root), "assets", StringComparison.OrdinalIgnoreCase))
        {
            root = Directory.GetParent(root)?.FullName ?? root;
        }

        if (!File.Exists(Path.Combine(root, "Vintagestory.exe")))
        {
            throw new FileNotFoundException("Vintagestory.exe was not found.", Path.Combine(root, "Vintagestory.exe"));
        }

        return root;
    }

    /// <summary>
    /// Executes the resolve User Data Root step used by the deterministic test Paths fixture.
    /// </summary>
    /// <returns>The resolve User Data Root result consumed by the caller&apos;s assertion.</returns>
    public static string ResolveUserDataRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("VINTAGE_STORY_DATA");
        string root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData")
            : Path.GetFullPath(configured);
        if (!Directory.Exists(Path.Combine(root, "Saves")))
        {
            throw new DirectoryNotFoundException($"Vintage Story Saves directory was not found below '{root}'.");
        }

        return root;
    }
}
