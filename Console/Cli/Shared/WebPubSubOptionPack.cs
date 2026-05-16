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

    protected override string ResourceType => "Microsoft.SignalRService/webPubSub";

    protected override async Task<WebPubSubResource> GetResourceCoreAsync(
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
        return (await rg.GetWebPubSubAsync(name, ct)).Value;
    }

    }
