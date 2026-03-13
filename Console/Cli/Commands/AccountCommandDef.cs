using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands;

public class AccountCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "account";
    public override string Description => "Manage Azure subscription information.";

    public readonly AccountListCommandDef List = new(auth);
    public readonly AccountListLocationsCommandDef ListLocations = new(auth);
}

/// <summary>List all available Azure subscriptions.</summary>
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
        var armClient = new ArmClient(_auth.GetCredential());

        var renderer = rendererFactory.CreateCollectionRenderer<SubscriptionResource>();
        await renderer.RenderAllAsync(
            System.Console.Out,
            FilterSubscriptions(armClient.GetSubscriptions().GetAllAsync(ct), ct),
            ct);

        return 0;
    }

    private async IAsyncEnumerable<object> FilterSubscriptions(
        IAsyncEnumerable<SubscriptionResource> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var sub in source.WithCancellation(ct))
        {
            if (!All && !sub.Data.State.Equals(SubscriptionState.Enabled))
                continue;
            yield return sub;
        }
    }
}

/// <summary>Show available locations for a subscription.</summary>
public partial class AccountListLocationsCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list-locations";
    public override string[] Aliases => ["locations"];
    public override string Description => "Show available locations for a subscription.";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var armClient = new ArmClient(_auth.GetCredential());
        var subscription = await Subscription.GetSubscriptionAsync(armClient);

        var renderer = rendererFactory.CreateCollectionRenderer<LocationExpanded>();
        await renderer.RenderAllAsync(
            System.Console.Out,
            subscription.GetLocations(cancellationToken: ct).ToAsyncEnumerableObjects(ct),
            ct);

        return 0;
    }
}
