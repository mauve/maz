using DotMake.CommandLine;

namespace Console.Shared;

public interface IConfirmationCommand
{
    /// <summary>
    /// Gets or sets a value indicating whether to prompt for confirmation before executing the command.
    /// </summary>
    [CliOption(
        Description = """
                This command requires confirmation before executing as it executes a potentially
                destructive operation. If this options is not set and the command is run non-interactively,
                then the command will fail with an error.

                If the command is run with --interactive then we will prompt for confirmation if
                this option is not specified.
            """,
        Aliases = ["-y", "--yes"],
        Required = false
    )]
    bool Confirm { get; set; }
}

public static class IConfirmationCommandExtensions
{
    public static void RequireConfirmation(this IConfirmationCommand self, bool interactive)
    {
        if (self.Confirm)
        {
            return;
        }

        if (!interactive || System.Console.IsInputRedirected)
        {
            throw new InvocationException(
                "This command requires confirmation before executing. Use --yes to confirm."
            );
        }

        System.Console.Write("Are you sure you want to continue? (y/N): ");
        var response = System.Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response != "y" && response != "yes")
        {
            throw new InvocationException("Operation cancelled by user.");
        }
    }
}
