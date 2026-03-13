using System.CommandLine;

namespace Console.Cli.Shared;

public class ConfirmationOptionPack : OptionPack
{
    public readonly Option<bool> Confirm;

    public ConfirmationOptionPack()
    {
        Confirm = new Option<bool>("--yes", ["-y", "--confirm"])
        {
            Description =
                """
            Confirms the operation without prompting. Required when running non-interactively.
            If omitted and --interactive is set, a prompt is shown.
            """
        };
    }

    internal override void AddOptionsTo(Command cmd) => cmd.Add(Confirm);

    public void RequireConfirmation(bool interactive)
    {
        if (GetValue(Confirm))
            return;

        if (!interactive || System.Console.IsInputRedirected)
            throw new InvocationException("This command requires confirmation. Use --yes to confirm.");

        System.Console.Write("Are you sure you want to continue? (y/N): ");
        var response = System.Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response != "y" && response != "yes")
            throw new InvocationException("Operation cancelled by user.");
    }
}
