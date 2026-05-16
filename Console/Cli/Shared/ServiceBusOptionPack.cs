using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ServiceBus;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Service Bus namespace by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --servicebus-namespace / --sbn:
///   namespace-name
///   rg/namespace-name
///   sub/rg/namespace-name
///   /s/{sub}/rg/namespace-name
///   /subscriptions/{guid}/rg/namespace-name
///   /arm/namespace-name
///</summary>
public partial class ServiceBusOptionPack
    : DataplaneResourceOptionPack<ServiceBusNamespaceResource, Uri>
{
    public override string HelpTitle => "Service Bus Namespace";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>
    /// Service Bus namespace name, or combined format: [sub/]rg/namespace-name (see section description).
    /// </summary>
    [CliOption(
        "--servicebus-namespace",
        "--sbn",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            ServiceBusOptionPack,
            ServiceBusNamespaceResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ServiceBusNamespace { get; }

    protected override string? RawResourceValue => ServiceBusNamespace;

    protected override Uri GetDataplaneRef(ServiceBusNamespaceResource resource) =>
        new($"https://{resource.Data.Name}.servicebus.windows.net");

    protected override string ResourceType => "Microsoft.ServiceBus/namespaces";

    protected override async Task<ServiceBusNamespaceResource> GetResourceCoreAsync(
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
        return (await rg.GetServiceBusNamespaceAsync(name, ct)).Value;
    }

}
