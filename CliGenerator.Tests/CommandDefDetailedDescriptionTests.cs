using System.CommandLine;
using System.Reflection;

namespace CliGenerator.Tests;

[TestClass]
public class CommandDefDetailedDescriptionTests
{
    [TestMethod]
    public void Build_RegistersDetailedDescription_OverRemarks()
    {
        var cmd = BuildCommand(new DetailedWinsCommand());
        var text = GetRegisteredDetailedDescription(cmd);

        Assert.AreEqual("Detailed text", text);
    }

    [TestMethod]
    public void Build_UsesRemarks_WhenDetailedDescriptionNotOverridden()
    {
        var cmd = BuildCommand(new RemarksOnlyCommand());
        var text = GetRegisteredDetailedDescription(cmd);

        Assert.AreEqual("Remarks text", text);
    }

    private static Command BuildCommand(global::Console.Cli.CommandDef def)
    {
        var build = typeof(global::Console.Cli.CommandDef).GetMethod(
            "Build",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        Assert.IsNotNull(build, "Expected internal CommandDef.Build method.");
        return (Command)build!.Invoke(def, null)!;
    }

    private static string? GetRegisteredDetailedDescription(Command cmd)
    {
        var asm = typeof(global::Console.Cli.CommandDef).Assembly;
        var remarksRegistryType = asm.GetType("Console.Cli.RemarksRegistry");

        Assert.IsNotNull(remarksRegistryType, "Expected Console.Cli.RemarksRegistry type.");

        var getMethod = remarksRegistryType!.GetMethod(
            "Get",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.IsNotNull(getMethod, "Expected internal RemarksRegistry.Get method.");

        return (string?)getMethod!.Invoke(null, [cmd]);
    }

    private sealed class DetailedWinsCommand : global::Console.Cli.CommandDef
    {
        public override string Name => "detailed-wins";
        public override string? DetailedDescription => "Detailed text";
        protected override string? Remarks => "Remarks text";
    }

    private sealed class RemarksOnlyCommand : global::Console.Cli.CommandDef
    {
        public override string Name => "remarks-only";
        protected override string? Remarks => "Remarks text";
    }
}
