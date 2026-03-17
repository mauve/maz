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
/// </summary>
public partial class EventHubOptionPack
    : DataplaneResourceOptionPack<EventHubsNamespaceResource, Uri>
{
    public override string ArmResourceType => "Microsoft.EventHub/namespaces";
    public override string HelpTitle => "Event Hubs Namespace";

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

    protected override async Task<EventHubsNamespaceResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSubscriptionId,
        string resolvedResourceGroupName,
        string resourceName,
        CancellationToken ct
    )
    {
        var sub = armClient.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{resolvedSubscriptionId}")
        );
        var rg = await sub.GetResourceGroupAsync(resolvedResourceGroupName, ct);
        return await rg.Value.GetEventHubsNamespaceAsync(resourceName, ct);
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
