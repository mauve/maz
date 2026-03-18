using Console.Cli.Commands;
using Console.Cli.Commands.Bootstrap;
using Console.Cli.Commands.Group;
using Console.Cli.Commands.JmesPath;
using Console.Cli.Parsing;
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
    protected override bool IsRootCommand => true;

    public readonly AuthOptionPack Auth;
    public readonly DiagnosticOptionPack Diagnostics;
    public readonly InteractiveOptionPack Interactive;
    public readonly GlobalBehaviorOptionPack GlobalBehavior;

    public readonly AccountCommandDef Account;
    public readonly AcrCommandDef Acr;
    public readonly GetTokenCommandDef GetToken;
    public readonly GroupCommandDef Group;
    public readonly CompletionCommandDef Completion;
    public readonly ConfigureCommandDef Configure;
    public readonly JmesPathCommandDef JmesPath;
    public readonly BootstrapCommandDef Bootstrap;
    public readonly LoginCommandDef Login;
    public readonly LogoutCommandDef Logout;

    partial void InitGeneratedCommands(string? targetService);

    public RootCommandDef(string? targetService = null)
    {
        Auth = new AuthOptionPack();
        Diagnostics = new DiagnosticOptionPack();
        Interactive = new InteractiveOptionPack();
        GlobalBehavior = new GlobalBehaviorOptionPack();
        Account = new AccountCommandDef(Auth);
        Acr = new AcrCommandDef(Auth);
        GetToken = new GetTokenCommandDef(Auth);
        Group = new GroupCommandDef(Auth);
        Completion = new CompletionCommandDef();
        Configure = new ConfigureCommandDef(Auth, Interactive);
        JmesPath = new JmesPathCommandDef(Auth);
        Bootstrap = new BootstrapCommandDef(Auth, Interactive);
        Login = new LoginCommandDef();
        Logout = new LogoutCommandDef();
        InitGeneratedCommands(targetService);
    }
}
