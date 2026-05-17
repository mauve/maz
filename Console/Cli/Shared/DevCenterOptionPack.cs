using System.Text.Json.Nodes;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.DevCenter;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Dev Center by name.
///
/// Accepted formats for --dev-center / --dc:
///   devcenter-name
///   rg/devcenter-name
///   sub/rg/devcenter-name
///   /arm/devcenter-name
///</summary>
public partial class DevCenterOptionPack : DataplaneResourceOptionPack<DevCenterResource, Uri>
{
    public override string HelpTitle => "Dev Center";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Dev Center name, or combined format: [sub/]rg/devcenter-name.</summary>
    [CliOption(
        "--dev-center",
        "--dc",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            DevCenterOptionPack,
            DevCenterResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? DevCenterName { get; }

    protected override string? RawResourceValue => DevCenterName;

    protected override string DataplaneArmApiVersion => "2023-04-01";

    protected override Uri GetDataplaneRefFromJson(JsonNode json) =>
        new(json["properties"]!["devCenterUri"]!.GetValue<string>());

    protected override string ResourceType => "Microsoft.DevCenter/devcenters";

    protected override Task<DevCenterResource> GetResourceCoreAsync(
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
        return Task.FromResult(armClient.GetDevCenterResource(id));
    }

}
