using System.CommandLine;
using Console.Cli.Commands;
using Console.Cli.Commands.Group;
using Console.Cli.Commands.Monitor;
using Console.Cli.Shared;

namespace Console.Cli;

public class RootCommandDef : CommandDef
{
    public override string Name => "maz";
    public override string Description => "A smaller az-cli tool";

    public readonly AuthOptionPack Auth;

    public readonly AccountCommandDef Account;
    public readonly GetTokenCommandDef GetToken;
    public readonly GroupCommandDef Group;
    public readonly MonitorCommandDef Monitor;
    public readonly CompletionCommandDef Completion;

    public RootCommandDef()
    {
        Auth = new AuthOptionPack();
        Account = new AccountCommandDef(Auth);
        GetToken = new GetTokenCommandDef(Auth);
        Group = new GroupCommandDef(Auth);
        Monitor = new MonitorCommandDef(Auth);
        Completion = new CompletionCommandDef();
    }

    protected override Command CreateCommand() => new RootCommand(Description);
}
