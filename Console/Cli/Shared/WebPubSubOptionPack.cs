using System.Text.Json.Nodes;
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

    protected override string DataplaneArmApiVersion => "2024-03-01";

    protected override Uri GetDataplaneRefFromJson(JsonNode json) =>
        new($"https://{json["properties"]!["hostName"]!.GetValue<string>()}");

    protected override string ResourceType => "Microsoft.SignalRService/webPubSub";

    protected override Task<WebPubSubResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSub,
        string resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var id = new ResourceIdentifier(
            $"/subscriptions/{resolvedSub}/resourceGroups/{resolvedRg}/providers/{ResourceType}/{name}"
        );
        return Task.FromResult(armClient.GetWebPubSubResource(id));
    }

}
