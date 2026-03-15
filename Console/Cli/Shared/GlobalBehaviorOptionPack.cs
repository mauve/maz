using System.CommandLine;

namespace Console.Cli.Shared;

/// <summary>
/// Global options that modify the behaviour of any maz command,
/// independent of the specific command being run.
/// </summary>
[CliManualOptions("--require-confirmation", "--no-require-confirmation")]
public class GlobalBehaviorOptionPack : OptionPack
{
    /// <summary>
    /// When set, any destructive operation (create / delete / update) requires explicit
    /// interactive confirmation before proceeding.
    /// Can also be enabled via <c>MAZ_REQUIRE_CONFIRMATION=true</c> or the config file.
    /// </summary>
    internal static readonly GlobalOption<bool> RequireConfirmationOption = new(
        "--require-confirmation",
        "Require interactive confirmation before any destructive (create/delete/update) operation."
    )
    {
        DefaultValueFactory = _ =>
            bool.TryParse(Environment.GetEnvironmentVariable("MAZ_REQUIRE_CONFIRMATION"), out var v)
            && v,
    };

    /// <summary>Whether destructive-operation confirmation is required.</summary>
    public bool RequireConfirmation => GetValue(RequireConfirmationOption);

    public override string HelpTitle => "Global Behavior";

    protected override void AddManualOptions(Command cmd) => cmd.Add(RequireConfirmationOption);
}
