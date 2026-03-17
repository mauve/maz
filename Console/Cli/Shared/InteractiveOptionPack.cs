using Console.Cli.Parsing;

namespace Console.Cli.Shared;

[CliManualOptions("--interactive", "--no-interactive")]
public class InteractiveOptionPack : OptionPack
{
    internal readonly CliOption<bool> _interactiveOption = new()
    {
        Name = "--interactive",
        Aliases = ["--no-interactive"],
        Description = "Allow interactive prompts. Defaults to true; auto-disabled when redirecting or TERM=dumb.",
        Recursive = true,
        DefaultValueFactory = () => true,
    };

    public bool Interactive => GetValue(_interactiveOption);

    public override string HelpTitle => "";

    internal override IEnumerable<CliOption> EnumerateManualOptions()
    {
        yield return _interactiveOption;
    }

    /// <summary>
    /// Effective interactivity: respects the option value and terminal conditions.
    /// </summary>
    public static bool IsEffectivelyInteractive(bool optionValue) =>
        optionValue
        && !System.Console.IsInputRedirected
        && !System.Console.IsOutputRedirected
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    /// <summary>
    /// Walk the command tree to find the InteractiveOptionPack and check effective interactivity.
    /// </summary>
    public static bool IsEffectivelyInteractiveFromTree(CommandDef cmd)
    {
        foreach (var opt in cmd.EnumerateAllOptions())
        {
            if (opt.Name == "--interactive" && opt is CliOption<bool> boolOpt)
                return IsEffectivelyInteractive(boolOpt.Value);
        }
        return IsEffectivelyInteractive(true);
    }
}
