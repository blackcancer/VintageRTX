using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

// Deployment acceptance tool, not a replacement for the native ModLoader or an in-game test.
// No compile-time reference to the mod: every mod-owned dependency must come from the built package.
try
{
    if (args.Length != 2) throw new ArgumentException("Usage: PackageProbe <mod-folder> <official-client-root>");
    string package = Path.GetFullPath(args[0]), game = Path.GetFullPath(args[1]);
    foreach (string name in new[] { "modinfo.json", "VintageRTX.dll", "VintageRTX.Core.dll" })
        if (!File.Exists(Path.Combine(package, name))) throw new FileNotFoundException("Incomplete mod package: " + name);
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "modinfo.json")));
    if (manifest.RootElement.GetProperty("type").GetString() != "code"
        || manifest.RootElement.GetProperty("modid").GetString() != "vintagertx"
        || !string.Equals(manifest.RootElement.GetProperty("side").GetString(), "Client", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The built mod manifest does not describe the client VintageRTX mod.");
    var context = new PackageContext(package, game);
    try
    {
        using var scope = context.EnterContextualReflection();
        Assembly api = context.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryAPI.dll"));
        Assembly core = context.LoadFromAssemblyPath(Path.Combine(package, "VintageRTX.Core.dll"));
        Assembly mod = context.LoadFromAssemblyPath(Path.Combine(package, "VintageRTX.dll"));
        if (mod.GetName().Name != "VintageRTX" || core.GetName().Name != "VintageRTX.Core")
            throw new InvalidDataException("Package filenames and assembly identities disagree.");
        if (!mod.GetReferencedAssemblies().Any(name => name.Name == core.GetName().Name))
            throw new InvalidDataException("The client does not reference the packaged core assembly.");
        Type baseType = api.GetType("Vintagestory.API.Common.ModSystem", throwOnError: true)!;
        Type sideType = api.GetType("Vintagestory.API.Common.EnumAppSide", throwOnError: true)!;
        Type[] hosts = mod.GetExportedTypes().Where(type => !type.IsAbstract && baseType.IsAssignableFrom(type)).ToArray();
        if (hosts.Length != 1) throw new InvalidDataException("Expected exactly one discoverable ModSystem.");
        object instance = Activator.CreateInstance(hosts[0])!;
        MethodInfo shouldLoad = hosts[0].GetMethod("ShouldLoad", new[] { sideType })!;
        bool client = (bool)shouldLoad.Invoke(instance, new[] { Enum.Parse(sideType, "Client") })!;
        bool server = (bool)shouldLoad.Invoke(instance, new[] { Enum.Parse(sideType, "Server") })!;
        if (!client || server) throw new InvalidDataException("The packaged host no longer selects the client only.");
        // Resolve all exported core types as well, without any copy in the probe's own output folder.
        if (core.GetExportedTypes().Length == 0) throw new InvalidDataException("The packaged core is empty.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true, modSystem = hosts[0].FullName, clientOnly = true,
            modAssembly = mod.Location, coreAssembly = core.Location, apiAssembly = api.Location,
            scope = "Built package assembly resolution and ModSystem selection; no game world started"
        }));
    }
    finally { context.Unload(); }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.ToString());
    return 1;
}

sealed class PackageContext(string package, string game) : AssemblyLoadContext("Deployment package probe", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        foreach (string directory in new[] { package, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
        {
            string file = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(file)) return LoadFromAssemblyPath(Path.GetFullPath(file));
        }
        if (name.Name?.StartsWith("VintageRTX", StringComparison.Ordinal) == true
            || name.Name?.StartsWith("Vintagestory", StringComparison.Ordinal) == true
            || name.Name == "Newtonsoft.Json")
            throw new FileNotFoundException("Missing package/game dependency: " + name.FullName);
        return null;
    }
}
