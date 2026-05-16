namespace Console.Cli.Commands.Debug;

/// <summary>Developer diagnostics and debugging tools.</summary>
public partial class DebugCommandDef : CommandDef
{
    public override string Name => "debug";
    protected internal override bool IsManualCommand => true;

    public readonly DebugSuggestCommandDef Suggest = new();
}
