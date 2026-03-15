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
///   /ehn/namespace-name
/// </summary>
public partial class EventHubOptionPack
    : DataplaneResourceOptionPack<EventHubsNamespaceResource, Uri>
{
    public const string ShortPathPrefix = "/ehn/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;

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

    protected override async Task<EventHubsNamespaceResource> GetResourceCoreAsync(
        ArmClient armClient,
        string? resolvedSub,
        string? resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var sub = await ResolveSubscriptionAsync(armClient, resolvedSub);

        if (resolvedRg is not null)
        {
            var rg = await sub.GetResourceGroupAsync(resolvedRg, ct);
            return await rg.Value.GetEventHubsNamespaceAsync(name, ct);
        }

        var matches = new List<EventHubsNamespaceResource>();
        await foreach (var ns in sub.GetEventHubsNamespacesAsync(cancellationToken: ct))
        {
            if (ns.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(ns);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Event Hubs namespace '{name}' not found in subscription."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} namespaces:\n"
                    + string.Join(
                        "\n",
                        matches.Select(m =>
                            $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"
                        )
                    )
            ),
        };
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
