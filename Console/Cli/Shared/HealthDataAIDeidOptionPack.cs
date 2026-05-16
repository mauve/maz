using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.HealthDataAIServices;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Health Data AI Services de-identification service by name.
///
/// Accepted formats for --deid-service / --deid:
///   service-name
///   rg/service-name
///   sub/rg/service-name
///   /arm/service-name
///</summary>
public partial class HealthDataAIDeidOptionPack
    : DataplaneResourceOptionPack<DeidServiceResource, Uri>
{
    public override string HelpTitle => "De-identification Service";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>De-identification service name, or combined format: [sub/]rg/service-name.</summary>
    [CliOption(
        "--deid-service",
        "--deid",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            HealthDataAIDeidOptionPack,
            DeidServiceResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ServiceName { get; }

    protected override string? RawResourceValue => ServiceName;

    protected override Uri GetDataplaneRef(DeidServiceResource resource) =>
        new Uri(resource.Data.Properties.ServiceUri!);

    protected override string ResourceType => "Microsoft.HealthDataAIServices/deidServices";

    protected override async Task<DeidServiceResource> GetResourceCoreAsync(
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
        return (await rg.GetDeidServiceAsync(name, ct)).Value;
    }

}
