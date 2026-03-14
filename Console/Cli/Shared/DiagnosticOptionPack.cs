using System.CommandLine;

namespace Console.Cli.Shared;

public class DiagnosticOptionPack : OptionPack
{
    /// <summary>
    /// Static reference so <see cref="CommandDef"/> can read the value from any
    /// <see cref="ParseResult"/> without needing a pack instance.
    /// </summary>
    internal static readonly GlobalOption<bool> DetailedErrorsOption = new(
        "--detailed-errors",
        ["--verbose"],
        "Show detailed error output including exception type and stack trace."
    );

    public bool DetailedErrors => GetValue(DetailedErrorsOption);

    public override string HelpTitle => "Diagnostics";

    protected override void AddManualOptions(Command cmd) => cmd.Add(DetailedErrorsOption);
}
