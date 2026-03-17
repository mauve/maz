using Azure.ResourceManager;
using Azure.ResourceManager.WebPubSub;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Web PubSub service by name.
///
/// Accepted formats for --web-pubsub / --wps:
///   service-name
///   rg/service-name
///   sub/rg/service-name
///   /arm/service-name
///</summary>
public partial class WebPubSubOptionPack : DataplaneResourceOptionPack<WebPubSubResource, Uri>
{
    public override string HelpTitle => "Web PubSub";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Web PubSub service name, or combined format: [sub/]rg/service-name.</summary>
    [CliOption(
        "--web-pubsub",
        "--wps",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            WebPubSubOptionPack,
            WebPubSubResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ServiceName { get; }

    protected override string? RawResourceValue => ServiceName;

    protected override Uri GetDataplaneRef(WebPubSubResource resource) =>
        new Uri("https://" + resource.Data.HostName);

    protected override async Task<WebPubSubResource> GetResourceCoreAsync(
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
            return await rg.Value.GetWebPubSubAsync(name, ct);
        }

        var matches = new List<WebPubSubResource>();
        await foreach (var svc in sub.GetWebPubSubsAsync(cancellationToken: ct))
        {
            if (svc.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(svc);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"Web PubSub service '{name}' not found in subscription."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} services:\n"
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
            await foreach (var s in rg.Value.GetWebPubSubs().GetAllAsync(cancellationToken: ct))
            {
                if (s.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(s.Data.Name);
            }
        }
        else
        {
            await foreach (var s in sub.GetWebPubSubsAsync(cancellationToken: ct))
            {
                if (s.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(s.Data.Name);
            }
        }

        return results;
    }
}
