namespace Console.Cli.Commands.Debug;

/// <summary>Developer diagnostics and debugging tools.</summary>
/// <remarks>Commands for inspecting completion candidates, tracing ARM resolution, and other internal diagnostics.</remarks>
public partial class DebugCommandDef : CommandDef
{
    public override string Name => "debug";
    protected internal override bool IsManualCommand => true;

    public readonly DebugSuggestCommandDef Suggest = new();
}
