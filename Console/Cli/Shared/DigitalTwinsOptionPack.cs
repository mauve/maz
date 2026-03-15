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
///   /dt/instance-name
/// </summary>
public partial class DigitalTwinsOptionPack : DataplaneResourceOptionPack<DigitalTwinsDescriptionResource, Uri>
{
    public const string ShortPathPrefix = "/dt/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;
    public override string HelpTitle => "Digital Twins";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly ResourceGroupOptionPack ResourceGroup = new();

    protected override SubscriptionOptionPack SubscriptionPack => Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Digital Twins instance name, or combined format: [sub/]rg/instance-name.</summary>
    [CliOption(
        "--digital-twins",
        "--dt",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<DigitalTwinsOptionPack, DigitalTwinsDescriptionResource>),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? InstanceName { get; }

    protected override string? RawResourceValue => InstanceName;

    protected override Uri GetDataplaneRef(DigitalTwinsDescriptionResource resource) =>
        new Uri("https://" + resource.Data.HostName);

    protected override async Task<DigitalTwinsDescriptionResource> GetResourceCoreAsync(
        ArmClient armClient,
        string? resolvedSub,
        string? resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var sub = await ResolveSubscriptionAsync(armClient, resolvedSub);

        if (resolvedRg is not null)
        {
            var rg = await sub.GetResourceGroupAsync(resolvedRg, ct);
            return await rg.Value.GetDigitalTwinsDescriptionAsync(name, ct);
        }

        var matches = new List<DigitalTwinsDescriptionResource>();
        await foreach (var dt in sub.GetDigitalTwinsDescriptionsAsync(cancellationToken: ct))
        {
            if (dt.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(dt);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException($"Digital Twins instance '{name}' not found in subscription."),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} instances:\n"
                    + string.Join("\n", matches.Select(m =>
                        $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"))
            ),
        };
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
            await foreach (var dt in rg.Value.GetDigitalTwinsDescriptions().GetAllAsync(cancellationToken: ct))
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
