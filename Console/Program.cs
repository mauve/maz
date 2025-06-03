using Console;
using DotMake.CommandLine;

try
{
    Cli.Run<RootCommand>(args);
}
catch (InvocationException ex)
{
    System.Console.Error.WriteLine(ex.Message);
    Environment.Exit(ex.ExitCode);
}
