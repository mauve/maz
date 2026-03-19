using Console.Cli.Parsing;

namespace Console.Cli.Shared;

/// <summary>
/// Global options that modify the behaviour of any maz command,
/// independent of the specific command being run.
/// </summary>
[CliManualOptions("--require-confirmation", "--no-require-confirmation")]
public class GlobalBehaviorOptionPack : OptionPack
{
    internal readonly CliOption<bool> _requireConfirmationOption = new()
    {
        Name = "--require-confirmation",
        Aliases = ["--no-require-confirmation"],
        Description =
            "Require interactive confirmation before any destructive (create/delete/update) operation.",
        Recursive = true,
        DefaultValueFactory = () =>
            bool.TryParse(Environment.GetEnvironmentVariable("MAZ_REQUIRE_CONFIRMATION"), out var v)
            && v,
    };

    /// <summary>Whether destructive-operation confirmation is required.</summary>
    public bool RequireConfirmation => GetValue(_requireConfirmationOption);

    public override string HelpTitle => "Global Behavior";

    internal override IEnumerable<CliOption> EnumerateManualOptions()
    {
        yield return _requireConfirmationOption;
    }
}
