using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VintageRTX.Client.Tests;

/// <summary>
/// The test host has already loaded its own Newtonsoft.Json before test discovery. The game ships
/// a different API under that assembly identity. Never redirect or patch the host's dependency:
/// execute native integration cases in an isolated context using the actual game libraries.
/// Only the test framework and framework runtime are shared. No game object crosses the boundary.
/// </summary>
internal static class GameTestIsolation
{
    internal static bool InvokeIfDefault(Type testType, string method)
    {
        if (AssemblyLoadContext.GetLoadContext(testType.Assembly) != AssemblyLoadContext.Default) return false;
        var context = new OfficialContext();
        // TypeConverter attributes contain assembly-qualified strings. Framework reflection must
        // resolve those names in the same game context, not create Default-context AssetLocations.
        using var reflectionScope = context.EnterContextualReflection();
        Type? isolatedType = null;
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(testType.Assembly.Location);
            isolatedType = assembly.GetType(testType.FullName!, throwOnError: true)!;
            isolatedType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object?[] { null });
            object instance = Activator.CreateInstance(isolatedType)!;
            isolatedType.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!.Invoke(instance, null);
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        finally
        {
            try { isolatedType?.GetMethod("Cleanup", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null); }
            finally { context.Unload(); }
        }
    }
    private sealed class OfficialContext : AssemblyLoadContext
    {
        private readonly string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")
            ?? throw new InvalidOperationException("Native integration tests require VINTAGE_STORY."));
        internal OfficialContext() : base("VintageRTX official game integration", isCollectible: true) { }
        protected override Assembly? Load(AssemblyName name)
        {
            // Assertions must retain the test host's exact exception types.
            if (name.Name?.StartsWith("Microsoft.VisualStudio.TestPlatform.TestFramework", StringComparison.Ordinal) == true)
                return Default.LoadFromAssemblyName(name);
            foreach (string directory in new[] { game, Path.Combine(game,"Lib"), Path.Combine(game,"Mods"), AppContext.BaseDirectory })
            {
                string file = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(file)) return LoadFromAssemblyPath(Path.GetFullPath(file));
            }
            // Framework libraries are resolved by the runtime. A missing native game dependency
            // must not silently bind to an unrelated version held by the MSTest host.
            if (name.Name == "Newtonsoft.Json" || name.Name?.StartsWith("Vintagestory",StringComparison.Ordinal)==true)
                throw new FileNotFoundException("Official game dependency not found: " + name.FullName);
            return null;
        }
    }
}
