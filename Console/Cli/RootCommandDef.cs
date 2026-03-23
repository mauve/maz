using System.Reflection;
using Console.Cli.Commands;
using Console.Cli.Commands.Bootstrap;
using Console.Cli.Commands.Copy;
using Console.Cli.Commands.Group;
using Console.Cli.Commands.Iam;
using Console.Cli.Commands.JmesPath;
using Console.Cli.Shared;

namespace Console.Cli;

/// <summary>A fast, lightweight Azure CLI built for speed.</summary>
/// <remarks>Run 'maz --help' for usage or 'maz --help-commands' to browse all commands.</remarks>
public partial class RootCommandDef : CommandDef
{
    public override string Name => "maz";
    protected override bool IsRootCommand => true;

    public override string Description => "A fast, lightweight Azure CLI built for speed.";

    protected override string? Remarks => null;

    public readonly AuthOptionPack Auth;
    public readonly DiagnosticOptionPack Diagnostics;
    public readonly InteractiveOptionPack Interactive;
    public readonly GlobalBehaviorOptionPack GlobalBehavior;

    public readonly AccountCommandDef Account;
    public readonly AcrCommandDef Acr;
    public readonly GetTokenCommandDef GetToken;
    public readonly GroupCommandDef Group;
    public readonly IamCommandDef Iam;
    public readonly CompletionCommandDef Completion;
    public readonly ConfigureCommandDef Configure;
    public readonly JmesPathCommandDef JmesPath;
    public readonly BootstrapCommandDef Bootstrap;
    public readonly CopyCommandDef Copy;
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
        Iam = new IamCommandDef(Auth);
        Completion = new CompletionCommandDef();
        Configure = new ConfigureCommandDef(Auth, Interactive);
        Copy = new CopyCommandDef(Auth, Interactive);
        JmesPath = new JmesPathCommandDef(Auth);
        Bootstrap = new BootstrapCommandDef(Auth, Interactive);
        Login = new LoginCommandDef();
        Logout = new LogoutCommandDef();
        InitGeneratedCommands(targetService);
    }

    internal static string GetVersion() =>
        Assembly
            .GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0-dev";

    protected override Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var version = GetVersion();
        System.Console.WriteLine($"maz {version} — A fast, lightweight Azure CLI built for speed.");
        System.Console.WriteLine();
        System.Console.WriteLine(
            "Run 'maz --help' for usage or 'maz --help-commands' to browse all commands."
        );
        return Task.FromResult(0);
    }
}
