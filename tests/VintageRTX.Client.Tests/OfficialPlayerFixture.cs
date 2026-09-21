using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace VintageRTX.Client.Tests;

/// <summary>
/// Initializes only the real client's Entity getter storage. The native interface includes
/// non-public members that DispatchProxy cannot implement. No game assembly is modified.
/// </summary>
internal static class OfficialPlayerFixture
{
    internal static IClientPlayer Create(EntityPlayer entity)
    {
        string game = Environment.GetEnvironmentVariable("VINTAGE_STORY")
            ?? throw new InvalidOperationException("Official client references are required.");
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(OfficialPlayerFixture).Assembly)!;
        Assembly library = context.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
        Type type = library.GetType("Vintagestory.Client.NoObf.ClientPlayer", throwOnError: true)!;
        var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(type);
        MethodInfo getter = type.GetProperty("Entity")!.GetMethod!;
        PopulateGetter(player, getter, entity, 0);
        if (!ReferenceEquals(player.Entity, entity))
            throw new InvalidOperationException("The real player Entity getter did not retain its fixture value.");
        return player;
    }

    // Supported getter shape: this -> field(s) -> optional delegated getter -> optional cast -> ret.
    // The supported client delegates ClientPlayer.Entity to its world-data object. Follow the
    // actual metadata tokens, not guessed field names or assumptions about a direct Entity field.
    private static void PopulateGetter(object instance, MethodInfo getter, EntityPlayer entity, int depth)
    {
        if (depth > 4) throw new InvalidOperationException("Native Entity getter delegation is too deep.");
        byte[] il = getter.GetMethodBody()?.GetILAsByteArray() ?? [];
        int cursor = 0;
        if (il.Length == 0 || il[cursor++] != 0x02) throw Changed(getter, il);
        object current = instance;
        while (cursor < il.Length)
        {
            byte opcode = il[cursor++];
            if (opcode == 0x7b) // ldfld
            {
                if (cursor + 4 > il.Length) throw Changed(getter, il);
                FieldInfo field = getter.Module.ResolveField(BitConverter.ToInt32(il, cursor))!; cursor += 4;
                if (typeof(Entity).IsAssignableFrom(field.FieldType) && field.FieldType.IsInstanceOfType(entity))
                { field.SetValue(current, entity); current = entity; }
                else
                {
                    object? value = field.GetValue(current);
                    if (value is null)
                    {
                        if (!field.FieldType.IsClass || field.FieldType.IsAbstract) throw Changed(getter, il);
                        value = RuntimeHelpers.GetUninitializedObject(field.FieldType); field.SetValue(current, value);
                    }
                    current = value;
                }
            }
            else if (opcode is 0x28 or 0x6f) // call/callvirt delegated getter, no parameters
            {
                if (cursor + 4 > il.Length) throw Changed(getter, il);
                MethodInfo delegated = (MethodInfo)getter.Module.ResolveMethod(BitConverter.ToInt32(il, cursor))!; cursor += 4;
                if (delegated.IsStatic || delegated.GetParameters().Length != 0) throw Changed(getter, il);
                PopulateGetter(current, delegated, entity, depth + 1); current = entity;
            }
            else if (opcode == 0x74) // castclass does not change storage
            { if (cursor + 4 > il.Length) throw Changed(getter, il); cursor += 4; }
            else if (opcode == 0x2a && cursor == il.Length && ReferenceEquals(current, entity)) return;
            else throw Changed(getter, il);
        }
        throw Changed(getter, il);
    }
    private static InvalidOperationException Changed(MethodInfo method, byte[] il) => new(
        "Native player fixture getter changed: " + method.DeclaringType?.FullName + "." + method.Name
        + "; IL=" + Convert.ToHexString(il));
}
