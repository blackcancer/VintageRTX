using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VintageRTX.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageRTX.Client.Tests;

[TestClass, DoNotParallelize]
public sealed class RewriteBootstrapTests
{
    public static void Initialize(TestContext? _) { }
    private sealed class Commands
    {
        internal readonly Dictionary<string, Delegate> Handlers = new();
        internal readonly Dictionary<string, ICommandArgumentParser[]> Arguments = new();
        internal readonly ICoreClientAPI Api;
        internal Commands(LifecycleHost host)
        {
            CommandArgumentParsers parsers = new(host.Api);
            var commands = Stub.Create<IChatCommandApi>((method, args) => {
                if(method.Name == "get_Parsers") return parsers;
                if(method.Name != "Create") throw new InvalidOperationException("Unexpected command API call: " + method.Name);
                string code = (string)args[0]!; IChatCommand? command = null;
                command = Stub.Create<IChatCommand>((m, a) => {
                    if(m.Name == "HandleWith") Handlers[code] = (Delegate)a[0]!;
                    else if(m.Name == "WithArgs") Arguments[code] = (ICommandArgumentParser[])a[0]!;
                    else if(m.Name != "WithDescription") throw new InvalidOperationException("Unexpected command builder call: " + m.Name);
                    return command;
                });
                return command;
            });
            Api = Stub.Create<ICoreClientAPI>((method, args) => method.Name == "get_ChatCommands" ? commands : method.Invoke(host.Api, args));
        }
        internal TextCommandResult Invoke(string code, object? value = null)
        {
            var parser = Stub.Create<ICommandArgumentParser>((m, _) => m.Name == "GetValue" ? value
                : m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null);
            try {return (TextCommandResult)Handlers[code].DynamicInvoke(new TextCommandCallingArgs {Parsers = new() {parser}})!;}
            catch(TargetInvocationException exception) when(exception.InnerException is not null)
            {ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw;}
        }
    }
    private sealed class MissingText { public override string ToString() => null!; }

    [TestMethod]
    public void BootstrapBeforeAndAfterAssetsExecutesEveryCommandAndDisposesRegistrations()
    {
        if(GameTestIsolation.InvokeIfDefault(typeof(RewriteBootstrapTests), nameof(BootstrapBeforeAndAfterAssetsExecutesEveryCommandAndDisposesRegistrations))) return;
        foreach(bool assetsFirst in new[] {false, true})
        {
            var host = new LifecycleHost(); var commands = new Commands(host); var system = new VintageRTXModSystem();
            Assert.IsTrue(system.ShouldLoad(EnumAppSide.Client)); Assert.IsFalse(system.ShouldLoad(EnumAppSide.Server));
            Assert.AreEqual(.15, system.ExecuteOrder());
            var server = Stub.Create<ICoreAPI>((m, _) => m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null);
            system.AssetsLoaded(server);
            if(assetsFirst) {system.AssetsLoaded(commands.Api); system.AssetsLoaded(commands.Api);}
            system.StartClientSide(commands.Api);
            if(!assetsFirst) system.AssetsLoaded(commands.Api);
            CollectionAssert.AreEquivalent(new[] {"vrtxrewrite", "vrtxemissions", "vrtxlightlab"}, commands.Handlers.Keys.ToArray());
            Assert.AreEqual(2, host.Renderers.Count);
            StringAssert.Contains(commands.Invoke("vrtxrewrite").StatusMessage, "Sources=0");
            StringAssert.Contains(commands.Invoke("vrtxemissions").StatusMessage, "revision=1");
            Assert.IsInstanceOfType<WordArgParser>(commands.Arguments["vrtxlightlab"][0]);
            commands.Arguments["vrtxlightlab"][0].SetValue("on");
            var parsed = (TextCommandResult)commands.Handlers["vrtxlightlab"].DynamicInvoke(new TextCommandCallingArgs
                {Parsers = commands.Arguments["vrtxlightlab"].ToList()})!;
            StringAssert.Contains(parsed.StatusMessage, "visible");
            foreach(string mode in new[] {"on", "off", "dark", "lit"}) Assert.IsNotNull(commands.Invoke("vrtxlightlab", mode));
            StringAssert.Contains(commands.Invoke("vrtxlightlab", null).StatusMessage, "Utilisation");
            StringAssert.Contains(commands.Invoke("vrtxlightlab", new MissingText()).StatusMessage, "Utilisation");
            system.Dispose(); system.Dispose(); Assert.AreEqual(0, host.Renderers.Count);
            Assert.IsTrue(host.Events.Values.All(callback => callback is null));
            StringAssert.Contains(commands.Invoke("vrtxrewrite").StatusMessage, "stopped");
            StringAssert.Contains(commands.Invoke("vrtxemissions").StatusMessage, "stopped");
            StringAssert.Contains(commands.Invoke("vrtxlightlab", "on").StatusMessage, "stopped");
        }
    }
}
