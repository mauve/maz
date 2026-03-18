namespace CliGenerator.Tests;

[TestClass]
public class CommandDefDetailedDescriptionTests
{
    [TestMethod]
    public void DetailedDescription_TakesPrecedence_OverRemarks()
    {
        var cmd = new DetailedWinsCommand();
        Assert.AreEqual("Detailed text", cmd.DetailedDescription);
    }

    [TestMethod]
    public void Remarks_UsedWhenDetailedDescriptionNotOverridden()
    {
        var cmd = new RemarksOnlyCommand();
        Assert.AreEqual("Remarks text", cmd.DetailedDescription);
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
