using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Vintagestory.API.Client;
using VintageRTX.Rendering;

namespace VintageRTX.Test;

/// <summary>Reproduces inherited shader hook rejection without launching a game or issuing OpenGL calls.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RawAlbedoHookTests
{
    private static int postfixCalls;

    /// <summary>One inherited method body must have one patch identity across sibling shader classes.</summary>
    [TestMethod]
    public void InheritedImplementationsResolveToOneDeclaredMethod()
    {
        MethodInfo inherited = MappedUse(typeof(InheritedUse), typeof(IUse));
        MethodInfo sibling = MappedUse(typeof(SiblingUse), typeof(IUse));
        MethodInfo declared = RawAlbedoCapture.ResolveDeclaredUseMethod(inherited);
        Assert.AreEqual(typeof(BaseUse), declared.DeclaringType);
        Assert.AreEqual(declared.DeclaringType, declared.ReflectedType);
        Assert.AreEqual(declared, RawAlbedoCapture.ResolveDeclaredUseMethod(sibling));
        Assert.AreEqual(declared, RawAlbedoCapture.ResolveDeclaredUseMethod(declared));
    }

    /// <summary>Canonicalization preserves overrides and follows interface dispatch rather than a hidden namesake.</summary>
    [TestMethod]
    public void OverridesAndHiddenNamesRetainTheirActualInterfaceDispatch()
    {
        MethodInfo actualOverride = RawAlbedoCapture.ResolveDeclaredUseMethod(
            MappedUse(typeof(OverriddenUse), typeof(IUse)));
        Assert.AreEqual(typeof(OverriddenUse), actualOverride.DeclaringType);
        Assert.AreNotEqual(actualOverride, actualOverride.GetBaseDefinition());
        MethodInfo hidden = RawAlbedoCapture.ResolveDeclaredUseMethod(
            MappedUse(typeof(HiddenUse), typeof(IUse)));
        Assert.AreEqual(typeof(BaseUse), hidden.DeclaringType);
    }

    /// <summary>Explicit private implementations remain patchable without guessing a public method by name.</summary>
    [TestMethod]
    public void ExplicitImplementationAndInvalidInputRemainUnambiguous()
    {
        MethodInfo explicitUse = RawAlbedoCapture.ResolveDeclaredUseMethod(
            MappedUse(typeof(ExplicitUse), typeof(IUse)));
        Assert.AreEqual(typeof(ExplicitUse), explicitUse.DeclaringType);
        Assert.AreEqual(explicitUse.DeclaringType, explicitUse.ReflectedType);
        Assert.IsTrue(explicitUse.IsPrivate);
        Assert.ThrowsException<ArgumentNullException>(() =>
            RawAlbedoCapture.ResolveDeclaredUseMethod(null!));
    }

    /// <summary>Exercises actual Harmony installation, inherited dispatch, and owner-scoped removal.</summary>
    [TestMethod]
    public void CanonicalHookExecutesOnceAndCanBeRemoved()
    {
        string owner = "vintagertx.test.raw-albedo-inheritance." + Guid.NewGuid().ToString("N");
        Harmony harmony = new(owner);
        MethodInfo method = RawAlbedoCapture.ResolveDeclaredUseMethod(
            MappedUse(typeof(InheritedUse), typeof(IUse)));
        postfixCalls = 0;
        BaseUse.nativeCalls = 0;
        try
        {
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(RawAlbedoHookTests), nameof(CountPostfix)));
            InvokeUse(new InheritedUse());
            InvokeUse(new SiblingUse());
            Assert.AreEqual(2, BaseUse.nativeCalls);
            Assert.AreEqual(2, postfixCalls);
        }
        finally
        {
            harmony.Unpatch(method, HarmonyPatchType.All, owner);
        }
        InvokeUse(new InheritedUse());
        Assert.AreEqual(3, BaseUse.nativeCalls);
        Assert.AreEqual(2, postfixCalls, "Removing our hook must leave the original implementation callable.");
    }

    /// <summary>
    /// Installs and removes hooks on the real client shader implementations. No shader Use method
    /// is invoked: this qualifies method identity and Harmony compatibility, not rendered pixels.
    /// </summary>
    [TestMethod]
    public void OfficialClientShaderImplementationsAcceptCanonicalHooks()
    {
        string root = TestPaths.ResolveGameRoot();
        Assembly? ResolveClientDependency(AssemblyLoadContext context, AssemblyName name)
        {
            string filename = Path.GetFileName(name.Name) + ".dll";
            foreach (string folder in new[] { root, Path.Combine(root, "Lib") })
            {
                string path = Path.Combine(folder, filename);
                if (File.Exists(path)) return context.LoadFromAssemblyPath(Path.GetFullPath(path));
            }
            return null;
        }
        AssemblyLoadContext.Default.Resolving += ResolveClientDependency;
        string owner = "vintagertx.test.raw-albedo-official." + Guid.NewGuid().ToString("N");
        Harmony harmony = new(owner);
        HashSet<MethodInfo> installed = [];
        try
        {
            string library = Path.Combine(root, "VintagestoryLib.dll");
            Assert.IsTrue(File.Exists(library), "The complete official client library is required; a stub is not accepted.");
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(library));
            Type[] shaderTypes = assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract
                && !type.ContainsGenericParameters && typeof(IShaderProgram).IsAssignableFrom(type)).ToArray();
            Assert.IsTrue(shaderTypes.Length >= 2, "The official terrain and entity shader implementations must be present.");
            int inheritedImplementations = 0;
            int inheritedAliases = 0;
            foreach (Type type in shaderTypes)
            {
                MethodInfo mapped = MappedUse(type, typeof(IShaderProgram));
                MethodInfo declared = RawAlbedoCapture.ResolveDeclaredUseMethod(mapped);
                Assert.AreEqual(declared.DeclaringType, declared.ReflectedType, type.FullName);
                Assert.IsFalse(declared.IsAbstract, type.FullName);
                Assert.IsNotNull(declared.GetMethodBody(), type.FullName);
                if (mapped.DeclaringType != type) inheritedImplementations++;
                if (mapped.DeclaringType != mapped.ReflectedType) inheritedAliases++;
                if (installed.Contains(declared)) continue;
                harmony.Patch(declared, postfix: new HarmonyMethod(typeof(RawAlbedoHookTests), nameof(CountPostfix)));
                installed.Add(declared);
            }
            Assert.IsTrue(inheritedImplementations > 0, "The real inherited Use implementation must be exercised.");
            Assert.IsTrue(inheritedAliases > 0, "The official interface map must reproduce the inherited MethodInfo alias.");
            Console.WriteLine($"Official shader hook evidence: types={shaderTypes.Length}, inherited={inheritedImplementations}, aliases={inheritedAliases}, unique implemented hooks={installed.Count}; no GL call or world launch.");
        }
        finally
        {
            try
            {
                foreach (MethodInfo method in installed) harmony.Unpatch(method, HarmonyPatchType.All, owner);
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= ResolveClientDependency;
            }
        }
    }

    /// <summary>Returns the interface's actual zero-argument Use implementation on a concrete type.</summary>
    /// <param name="type">Concrete implementation type.</param>
    /// <param name="contract">Interface defining Use.</param>
    /// <returns>Interface dispatch target, including inherited or explicit implementations.</returns>
    private static MethodInfo MappedUse(Type type, Type contract)
    {
        InterfaceMapping map = type.GetInterfaceMap(contract);
        int slot = Array.FindIndex(map.InterfaceMethods, method => method.Name == "Use"
            && method.GetParameters().Length == 0);
        Assert.IsTrue(slot >= 0, type.FullName);
        return map.TargetMethods[slot];
    }

    /// <summary>Counts calls delivered by a test-owned Harmony postfix.</summary>
    private static void CountPostfix() => postfixCalls++;

    /// <summary>Prevents inlining from bypassing a test-installed interface hook.</summary>
    /// <param name="instance">Object whose interface implementation is invoked.</param>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void InvokeUse(IUse instance) => instance.Use();

    /// <summary>Minimal dispatch contract used independently of the real client qualification above.</summary>
    private interface IUse
    {
        /// <summary>Activates a fixture without accessing OpenGL.</summary>
        void Use();
    }

    /// <summary>Models the official shader class that owns the inherited method body.</summary>
    private class BaseUse : IUse
    {
        /// <summary>Counts original method calls independently of the hook.</summary>
        internal static int nativeCalls;
        /// <summary>Implements the interface at the base declaration.</summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public virtual void Use() => nativeCalls++;
    }

    /// <summary>First shader family inheriting the same activation body.</summary>
    private sealed class InheritedUse : BaseUse { }
    /// <summary>Second shader family used to verify canonical deduplication.</summary>
    private sealed class SiblingUse : BaseUse { }
    /// <summary>Models a modded shader with a genuinely distinct activation body.</summary>
    private sealed class OverriddenUse : BaseUse
    {
        /// <summary>Overrides the interface implementation without calling the base.</summary>
        public override void Use() { }
    }
    /// <summary>Models a namesake that does not replace the inherited interface dispatch slot.</summary>
    private sealed class HiddenUse : BaseUse
    {
        /// <summary>Hides the public name without reimplementing the interface.</summary>
        public new void Use() { }
    }
    /// <summary>Models a private explicit activation implementation.</summary>
    private sealed class ExplicitUse : IUse
    {
        /// <summary>Implements Use explicitly instead of exposing a public namesake.</summary>
        void IUse.Use() { }
    }
}
