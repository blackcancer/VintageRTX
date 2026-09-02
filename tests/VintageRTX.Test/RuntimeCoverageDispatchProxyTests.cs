using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageRTX.Test;

/// <summary>
/// Verifies compatibility of deterministic interface doubles with Vintage
/// Story API visibility changes.
/// </summary>
[TestClass]
public sealed class RuntimeCoverageDispatchProxyTests
{
    /// <summary>
    /// Names the 1.22.7 internal interface member without asking the compiler to bind an
    /// inaccessible symbol from the installed binary reference.
    /// </summary>
    private const string InteractionRangeMethodName = "IsInInteractionRangeOf";

    /// <summary>
    /// Verifies that the internal abstract IPlayer interaction-range overload
    /// introduced by Vintage Story 1.22.7 is emitted and forwarded without a
    /// ProxyBuilder <see cref="TypeLoadException"/>.
    /// </summary>
    [TestMethod]
    public void InaccessibleInheritedInterfaceMethodUsesAccessBypassProxy()
    {
        List<MethodInfo> forwarded = [];
        IClientPlayer player = RuntimeCoverageDispatchProxy.Create<IClientPlayer>((method, _) =>
        {
            forwarded.Add(method);
            return method.Name == "get_CameraYaw"
                ? 0.75f
                : RuntimeCoverageDispatchProxy.DefaultValue(method.ReturnType);
        });

        Assert.AreEqual(0.75f, player.CameraYaw);
        MethodInfo interactionRange = player.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == InteractionRangeMethodName
                && method.GetParameters()[0].ParameterType == typeof(BlockPos));
        Assert.IsFalse((bool)interactionRange.Invoke(player, [new BlockPos(1, 2, 3), 0.25f])!);
        Assert.IsTrue(forwarded.Any(method => method.Name == InteractionRangeMethodName
            && !method.IsPublic));
    }
}
