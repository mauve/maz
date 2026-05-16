using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.EventHubs;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Event Hubs namespace by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --eventhub-namespace / --ehn:
///   namespace-name
///   rg/namespace-name
///   sub/rg/namespace-name
///   /s/{sub}/rg/namespace-name
///   /subscriptions/{guid}/rg/namespace-name
///   /arm/namespace-name
///</summary>
public partial class EventHubOptionPack
    : DataplaneResourceOptionPack<EventHubsNamespaceResource, Uri>
{
    public override string HelpTitle => "Event Hubs Namespace";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>
    /// Event Hubs namespace name, or combined format: [sub/]rg/namespace-name (see section description).
    /// </summary>
    [CliOption(
        "--eventhub-namespace",
        "--ehn",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            EventHubOptionPack,
            EventHubsNamespaceResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? EventHubNamespace { get; }

    protected override string? RawResourceValue => EventHubNamespace;

    protected override Uri GetDataplaneRef(EventHubsNamespaceResource resource) =>
        new($"https://{resource.Data.Name}.servicebus.windows.net");

    protected override string ResourceType => "Microsoft.EventHub/namespaces";

    protected override async Task<EventHubsNamespaceResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSub,
        string resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var rgId = new ResourceIdentifier(
            $"/subscriptions/{resolvedSub}/resourceGroups/{resolvedRg}"
        );
        var rg = armClient.GetResourceGroupResource(rgId);
        return (await rg.GetEventHubsNamespaceAsync(name, ct)).Value;
    }

    public override async Task<IEnumerable<string>> GetCompletionCandidatesAsync(
        ArmClient armClient,
        string? subHint,
        string? rgHint,
        string prefix,
        CancellationToken ct = default
    )
    {
        var sub = await ResolveSubscriptionAsync(armClient, subHint);
        var results = new List<string>();

        if (rgHint is not null)
        {
            var rg = await sub.GetResourceGroupAsync(rgHint, ct);
            await foreach (
                var ns in rg.Value.GetEventHubsNamespaces().GetAllAsync(cancellationToken: ct)
            )
            {
                if (ns.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(ns.Data.Name);
            }
        }
        else
        {
            await foreach (var ns in sub.GetEventHubsNamespacesAsync(cancellationToken: ct))
            {
                if (ns.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(ns.Data.Name);
            }
        }

        return results;
    }
}
