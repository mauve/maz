using DotMake.CommandLine;

namespace Console;

[CliCommand(
    Description = "Display help information for the CLI.",
    Name = "help",
    Aliases = ["h", "?"],
    Parent = typeof(RootCommand)
)]
public class HelpCommand
{
    public void Run(CliContext context)
    {
        context.ShowHierarchy();
    }
}
