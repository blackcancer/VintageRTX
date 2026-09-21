using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client.Tests;

/// <summary>
/// Uses the supported client's actual IClientPlayer implementation as a data-only fixture.
/// DispatchProxy cannot legally implement the game's non-public player interface members.
/// No method or interface in a game assembly is patched, rewritten or made public.
/// Only Entity is used by these tests; world/render/event behavior remains explicitly provided.
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
        // Calling a live client constructor would start unrelated engine services. This fixture
        // exercises only its real Entity getter; every required backing field is verified below.
        var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(selected);
        int assigned = 0;
        for (Type? current = selected; current is not null; current = current.BaseType)
            foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (typeof(EntityPlayer).IsAssignableFrom(field.FieldType) && field.FieldType.IsInstanceOfType(entity))
                { field.SetValue(player, entity); assigned++; }
        if (assigned == 0 || !ReferenceEquals(player.Entity, entity))
            throw new InvalidOperationException("The official player's Entity storage changed; update the fixture, not the API.");
        return player;
    }
}
