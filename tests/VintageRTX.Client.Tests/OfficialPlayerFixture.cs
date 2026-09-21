using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Client.Tests;

/// <summary>
/// Data-only fixture using the supported client's real IClientPlayer implementation.
/// DispatchProxy cannot implement its non-public interface members. No game assembly is patched.
/// </summary>
internal static class OfficialPlayerFixture
{
    internal static IClientPlayer Create(EntityPlayer entity)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY")
            ?? throw new InvalidOperationException("The official client is required for this fixture.");
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(OfficialPlayerFixture).Assembly)!;
        Assembly library = context.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
        Type[] implementations = library.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface
            && typeof(IClientPlayer).IsAssignableFrom(t)).ToArray();
        Type? selected = implementations.SingleOrDefault(t => t.FullName == "Vintagestory.Client.NoObf.ClientPlayer");
        selected ??= implementations.Length == 1 ? implementations[0] : null;
        if (selected is null) throw new InvalidOperationException("Ambiguous native player implementations: "
            + string.Join(", ", implementations.Select(t => t.FullName)));
        var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(selected);
        var fields = new List<FieldInfo>();
        for (Type? current = selected; current is not null; current = current.BaseType)
            fields.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        int assigned = 0;
        foreach (FieldInfo field in fields)
            // The implementation may store the controlled player through its Entity/EntityAgent
            // base type, while the public getter returns EntityPlayer. Preserve that actual type.
            if (typeof(Entity).IsAssignableFrom(field.FieldType) && field.FieldType.IsInstanceOfType(entity))
            { field.SetValue(player, entity); assigned++; }
        if (assigned == 0 || !ReferenceEquals(player.Entity, entity))
        {
            MethodInfo getter = selected.GetProperty("Entity")!.GetMethod!;
            byte[] il = getter.GetMethodBody()?.GetILAsByteArray() ?? [];
            throw new InvalidOperationException("Native player fixture storage needs updating: " + selected.FullName
                + "; assigned=" + assigned + "; fields=" + string.Join(",", fields.Select(f => f.FieldType.FullName + " " + f.Name))
                + "; getterIL=" + Convert.ToHexString(il));
        }
        return player;
    }
}
