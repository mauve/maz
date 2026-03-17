using Azure.Core;
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
/// </summary>
public partial class WebPubSubOptionPack : DataplaneResourceOptionPack<WebPubSubResource, Uri>
{
    public override string ArmResourceType => "Microsoft.SignalRService/webPubSub";
    public override string HelpTitle => "Web PubSub";

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
        return await rg.Value.GetWebPubSubAsync(resourceName, ct);
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
