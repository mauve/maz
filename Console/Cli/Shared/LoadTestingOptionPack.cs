using System.Text.Json.Nodes;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.LoadTesting;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Load Testing resource by name.
///
/// Accepted formats for --load-test / --lt:
///   resource-name
///   rg/resource-name
///   sub/rg/resource-name
///   /arm/resource-name
///</summary>
public partial class LoadTestingOptionPack : DataplaneResourceOptionPack<LoadTestingResource, Uri>
{
    public override string HelpTitle => "Load Testing";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Load Testing resource name, or combined format: [sub/]rg/resource-name.</summary>
    [CliOption(
        "--load-test",
        "--lt",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            LoadTestingOptionPack,
            LoadTestingResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ResourceName { get; }

    protected override string? RawResourceValue => ResourceName;

    protected override string DataplaneArmApiVersion => "2022-12-01";

    protected override Uri GetDataplaneRefFromJson(JsonNode json) =>
        new(json["properties"]!["dataPlaneUri"]!.GetValue<string>());

    protected override string ResourceType => "Microsoft.LoadTestService/loadTests";

    protected override Task<LoadTestingResource> GetResourceCoreAsync(
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
        return Task.FromResult(armClient.GetLoadTestingResource(id));
    }

}
