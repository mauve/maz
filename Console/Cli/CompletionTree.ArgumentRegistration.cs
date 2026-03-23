namespace Console.Cli;

internal static partial class CompletionTree
{
    static CompletionTree()
    {
        CliArgumentCompletionRegistry.Register(
            "maz completion",
            [["bash", "zsh", "fish", "pwsh"]]
        );
    }
}
