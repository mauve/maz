using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.DigitalTwins;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Digital Twins instance by name.
///
/// Accepted formats for --digital-twins / --dt:
///   instance-name
///   rg/instance-name
///   sub/rg/instance-name
///   /arm/instance-name
///</summary>
public partial class DigitalTwinsOptionPack
    : DataplaneResourceOptionPack<DigitalTwinsDescriptionResource, Uri>
{
    public override string HelpTitle => "Digital Twins";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Digital Twins instance name, or combined format: [sub/]rg/instance-name.</summary>
    [CliOption(
        "--digital-twins",
        "--dt",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            DigitalTwinsOptionPack,
            DigitalTwinsDescriptionResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? InstanceName { get; }

    protected override string? RawResourceValue => InstanceName;

    protected override Uri GetDataplaneRef(DigitalTwinsDescriptionResource resource) =>
        new Uri("https://" + resource.Data.HostName);

    protected override string ResourceType => "Microsoft.DigitalTwins/digitalTwinsInstances";

    protected override async Task<DigitalTwinsDescriptionResource> GetResourceCoreAsync(
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
        return (await rg.GetDigitalTwinsDescriptionAsync(name, ct)).Value;
    }

}
