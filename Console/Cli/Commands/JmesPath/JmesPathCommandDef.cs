using Console.Cli.Shared;

namespace Console.Cli.Commands.JmesPath;

/// <summary>JMESPath query tools.</summary>
/// <remarks>
/// Commands for interactively building and testing JMESPath expressions against JSON data.
/// Use the editor subcommand to launch the TUI explorer.
/// </remarks>
public partial class JmesPathCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "jmespath";
    public override string[] Aliases => ["jmes"];
    protected internal override bool IsManualCommand => true;

    public readonly JmesPathEditorCommandDef Editor = new(auth);
}
