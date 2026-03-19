using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Console.Cli.Auth;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands;

/// <summary>Manage Azure subscription information.</summary>
/// <remarks>
/// Use this command group to inspect subscriptions available to the current identity.
/// Commands in this group can list accessible subscriptions and show supported regions.
/// </remarks>
public partial class AccountCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "account";
    protected internal override bool IsManualCommand => true;

    public readonly AccountShowCommandDef Show = new();
    public readonly AccountListCommandDef List = new(auth);
    public readonly AccountListLocationsCommandDef ListLocations = new(auth);
}

/// <summary>List all available Azure subscriptions.</summary>
/// <remarks>
/// By default, only enabled subscriptions are returned.
/// Use --all to include subscriptions in other states.
/// </remarks>
public partial class AccountListCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list";
    public override string[] Aliases => ["ls"];

    /// <summary>List all subscriptions from all clouds, including those not enabled.</summary>
    [CliOption("--all")]
    public partial bool All { get; }

    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var armClient = new ArmClient(cred);

        var renderer = rendererFactory.CreateCollectionRenderer<SubscriptionResource>();
        await renderer.RenderAllAsync(
            System.Console.Out,
            FilterSubscriptions(armClient.GetSubscriptions().GetAllAsync(ct), ct),
            ct
        );

        return 0;
    }

    private async IAsyncEnumerable<object> FilterSubscriptions(
        IAsyncEnumerable<SubscriptionResource> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await foreach (var sub in source.WithCancellation(ct))
        {
            if (!All && !sub.Data.State.Equals(SubscriptionState.Enabled))
                continue;
            yield return sub;
        }
    }
}

/// <summary>Show the current logged-in account and default subscription.</summary>
/// <remarks>
/// Displays information about the currently authenticated identity from the MSAL
/// token cache, including username, tenant, and the default subscription if configured.
///
/// This command does not make any network calls — it reads directly from the local
/// token cache and configuration.
/// </remarks>
public partial class AccountShowCommandDef : CommandDef
{
    public override string Name => "show";

    public readonly RenderOptionPack Render = new();

    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cache = new MsalCache(log);
        var accounts = cache.GetAccounts();

        if (accounts.Count == 0)
        {
            System.Console.Error.WriteLine("Not logged in. Run 'maz login' to authenticate.");
            return Task.FromResult(1);
        }

        var defaultSubId = Config.MazConfig.Current.DefaultSubscriptionId;

        if (Render.Format is not null)
        {
            // Structured output for the first (primary) account.
            var primary = accounts[0];
            var obj = new
            {
                username = primary.Username ?? "",
                tenantId = primary.TenantId ?? "",
                homeAccountId = primary.HomeAccountId,
                environment = primary.Environment,
                defaultSubscription = defaultSubId ?? "",
                accountCount = accounts.Count,
            };
            var renderer = Render.GetRendererFactory().CreateRendererForType<object>();
            return renderer.RenderAsync(System.Console.Out, obj, ct)
                .ContinueWith(_ => 0, ct);
        }

        // Default text output.
        var first = accounts[0];
        var entries = new List<(string, string)>();

        if (first.Username is not null)
            entries.Add(("User", Ansi.White(first.Username)));
        if (first.TenantId is not null)
            entries.Add(("Tenant", first.TenantId));
        entries.Add(("Environment", first.Environment));

        if (defaultSubId is not null)
            entries.Add(("Default Subscription", defaultSubId));

        // Show token status.
        var token = cache.FindAccessToken(
            "https://management.azure.com/.default",
            first.TenantId
        );
        if (token is not null)
            entries.Add(("Token Expires", token.ExpiresOn.ToString("yyyy-MM-dd HH:mm:ss UTC")));
        else
            entries.Add(("Token", Ansi.Yellow("expired or not cached (will refresh on next command)")));

        if (accounts.Count > 1)
            entries.Add(("Other Accounts", $"{accounts.Count - 1} additional"));

        DefinitionList.Write(System.Console.Out, entries);
        return Task.FromResult(0);
    }
}

/// <summary>Show available locations for a subscription.</summary>
/// <remarks>
/// This command resolves the target subscription and prints available Azure regions.
/// The output can be rendered in different formats through shared output options.
/// </remarks>
public partial class AccountListLocationsCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list-locations";
    public override string[] Aliases => ["locations"];

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var armClient = new ArmClient(cred);
        var subscription = await Subscription.GetSubscriptionAsync(armClient);

        var renderer = rendererFactory.CreateCollectionRenderer<LocationExpanded>();
        await renderer.RenderAllAsync(
            System.Console.Out,
            subscription.GetLocations(cancellationToken: ct).ToAsyncEnumerableObjects(ct),
            ct
        );

        return 0;
    }
}
