using Console.Cli.Commands.Browse;
using Console.Cli.Http;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Tui;

namespace Console.Cli.Commands.Generated;

/// <summary>Interactively browse blobs in an Azure Storage account.</summary>
/// <remarks>
/// Opens a TUI for navigating containers and blobs as a virtual folder tree.
/// Supports glob and tag-based filtering, multi-selection, and bulk actions
/// (download, delete, export, tag).
///
/// Target formats:
///   Account only:    myaccount
///   With container:  myaccount/mycontainer
///   With prefix:     myaccount/mycontainer/some/prefix
///   Full URL:        https://myaccount.blob.core.windows.net/mycontainer
///
/// Examples:
///   maz storage browse myaccount
///   maz storage browse myaccount/logs
///   maz storage browse myaccount/logs/2024 --include '*.json'
/// </remarks>
public partial class StorageBrowseCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "browse";
    protected internal override bool IsManualCommand => true;
    protected internal override bool IsDataPlane => true;

    private readonly AuthOptionPack _auth = auth;

    // ── Positional argument ───────────────────────────────────────────

    public readonly CliArgument<string> Target = new()
    {
        Name = "target",
        Description =
            "Storage target: account, account/container, or account/container/prefix.",
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return Target;
    }

    // ── Options ───────────────────────────────────────────────────────

    /// <summary>SAS token for authentication.</summary>
    [CliOption("--sas-token", Advanced = true)]
    public partial string? SasToken { get; }

    /// <summary>Storage account key for SharedKey authentication.</summary>
    [CliOption("--account-key", Advanced = true)]
    public partial string? AccountKey { get; }

    /// <summary>Initial glob filter pattern.</summary>
    [CliOption("--include")]
    public partial string? Include { get; }

    /// <summary>Initial tag query expression.</summary>
    [CliOption("--tag-query", Advanced = true)]
    public partial string? TagQuery { get; }

    // ── Execution ─────────────────────────────────────────────────────

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!InteractiveOptionPack.IsEffectivelyInteractive(true))
            throw new InvocationException(
                "The browse command requires an interactive terminal."
            );

        var targetRaw = Target.Value
            ?? throw new InvocationException(
                "A target is required: account, account/container, or account/container/prefix."
            );

        var log = DiagnosticOptionPack.GetLog();
        var target = StorageTargetHelper.ParseTarget(targetRaw);
        var blobAuth = StorageTargetHelper.BuildAuthStrategy(
            target.Account, SasToken, AccountKey, _auth, log);
        var client = new BlobRestClient(blobAuth, log);

        if (DiagnosticOptionPack.GetVerboseLevel() > 0)
        {
            System.Console.Error.WriteLine();
            System.Console.Error.Write("[verbose] Press Enter to launch TUI...");
            System.Console.ReadLine();
        }

        await using var app = new BrowseTuiApp(
            client,
            target.Account,
            target.Container,
            target.Prefix,
            Include,
            TagQuery
        );
        await app.RunAsync(ct);
        return 0;
    }
}
