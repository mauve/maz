using System.CommandLine;

namespace Console.Cli.Shared;

public class InteractiveOptionPack : OptionPack
{
    internal static readonly GlobalOption<bool> InteractiveOption = new(
        "--interactive",
        ["--no-interactive"],
        "Allow interactive prompts. Defaults to true; auto-disabled when redirecting or TERM=dumb."
    ) { DefaultValueFactory = _ => true };

    public bool Interactive => GetValue(InteractiveOption);

    public override string HelpTitle => "";

    protected override void AddManualOptions(Command cmd) => cmd.Add(InteractiveOption);

    /// <summary>
    /// Effective interactivity: respects the option value and terminal conditions.
    /// </summary>
    public static bool IsEffectivelyInteractive(bool optionValue) =>
        optionValue
        && !System.Console.IsInputRedirected
        && !System.Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("TERM") != "dumb";
}
