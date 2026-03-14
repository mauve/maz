namespace Console.Cli.Shared;

public partial class ConfirmationOptionPack : OptionPack
{
    /// <summary>
    /// Confirms the operation without prompting. Required when running non-interactively.
    /// If omitted and --interactive is set, a prompt is shown.
    /// </summary>
    [CliOption("--yes", "-y")]
    public partial bool Confirm { get; }

    public override string HelpTitle => "Confirmation";

    public void RequireConfirmation(bool interactive)
    {
        if (Confirm)
            return;

        if (!interactive || System.Console.IsInputRedirected)
            throw new InvocationException(
                "This command requires confirmation. Use --yes to confirm."
            );

        System.Console.Write("Are you sure you want to continue? (y/N): ");
        var response = System.Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response != "y" && response != "yes")
            throw new InvocationException("Operation cancelled by user.");
    }
}
