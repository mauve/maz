using System.CommandLine;
using Console.Cli.Commands;
using Console.Cli.Commands.Group;
using Console.Cli.Commands.Monitor;
using Console.Cli.Shared;

namespace Console.Cli;

/// <summary>Root command for the maz CLI.</summary>
/// <remarks>
/// The root command defines the entry point and wires all top-level command groups.
/// It also initializes shared authentication options used across subcommands.
/// </remarks>
public partial class RootCommandDef : CommandDef
{
    public override string Name => "maz";

    public readonly AuthOptionPack Auth;
    public readonly DiagnosticOptionPack Diagnostics;
    public readonly InteractiveOptionPack Interactive;
    public readonly GlobalBehaviorOptionPack GlobalBehavior;

    public readonly AccountCommandDef Account;
    public readonly GetTokenCommandDef GetToken;
    public readonly GroupCommandDef Group;
    public readonly MonitorCommandDef Monitor;
    public readonly CompletionCommandDef Completion;
    public readonly ConfigureCommandDef Configure;

    partial void InitGeneratedCommands();

    public RootCommandDef()
    {
        Auth = new AuthOptionPack();
        Diagnostics = new DiagnosticOptionPack();
        Interactive = new InteractiveOptionPack();
        GlobalBehavior = new GlobalBehaviorOptionPack();
        Account = new AccountCommandDef(Auth);
        GetToken = new GetTokenCommandDef(Auth);
        Group = new GroupCommandDef(Auth);
        Monitor = new MonitorCommandDef(Auth);
        Completion = new CompletionCommandDef();
        Configure = new ConfigureCommandDef(Auth, Interactive);
        InitGeneratedCommands();
    }

    protected override Command CreateCommand() => new RootCommand(Description);
}
