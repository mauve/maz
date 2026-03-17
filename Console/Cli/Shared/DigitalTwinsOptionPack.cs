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
/// </summary>
public partial class DigitalTwinsOptionPack
    : DataplaneResourceOptionPack<DigitalTwinsDescriptionResource, Uri>
{
    public override string ArmResourceType => "Microsoft.DigitalTwins/digitalTwinsInstances";
    public override string HelpTitle => "Digital Twins";

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

    protected override async Task<DigitalTwinsDescriptionResource> GetResourceCoreAsync(
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
        return await rg.Value.GetDigitalTwinsDescriptionAsync(resourceName, ct);
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
            await foreach (
                var dt in rg.Value.GetDigitalTwinsDescriptions().GetAllAsync(cancellationToken: ct)
            )
            {
                if (dt.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(dt.Data.Name);
            }
        }
        else
        {
            await foreach (var dt in sub.GetDigitalTwinsDescriptionsAsync(cancellationToken: ct))
            {
                if (dt.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(dt.Data.Name);
            }
        }

        return results;
    }
}
