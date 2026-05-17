using System.Text.Json.Nodes;
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

    protected override string DataplaneArmApiVersion => "2024-01-01";

    protected override Uri GetDataplaneRefFromJson(JsonNode json) =>
        new($"https://{json["name"]!.GetValue<string>()}.servicebus.windows.net");

    protected override string ResourceType => "Microsoft.EventHub/namespaces";

    protected override Task<EventHubsNamespaceResource> GetResourceCoreAsync(
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
        return Task.FromResult(armClient.GetEventHubsNamespaceResource(id));
    }

}
