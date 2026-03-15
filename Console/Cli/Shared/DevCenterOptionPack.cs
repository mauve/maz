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
///   /dc/devcenter-name
/// </summary>
public partial class DevCenterOptionPack : DataplaneResourceOptionPack<DevCenterResource, Uri>
{
    public const string ShortPathPrefix = "/dc/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;
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

    protected override Uri GetDataplaneRef(DevCenterResource resource) =>
        resource.Data.DevCenterUri!;

    protected override async Task<DevCenterResource> GetResourceCoreAsync(
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
            return await rg.Value.GetDevCenterAsync(name, ct);
        }

        var matches = new List<DevCenterResource>();
        await foreach (var dc in sub.GetDevCentersAsync(cancellationToken: ct))
        {
            if (dc.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(dc);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException($"Dev Center '{name}' not found in subscription."),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} dev centers:\n"
                    + string.Join(
                        "\n",
                        matches.Select(m =>
                            $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"
                        )
                    )
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
            await foreach (var dc in rg.Value.GetDevCenters().GetAllAsync(cancellationToken: ct))
            {
                if (dc.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(dc.Data.Name);
            }
        }
        else
        {
            await foreach (var dc in sub.GetDevCentersAsync(cancellationToken: ct))
            {
                if (dc.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(dc.Data.Name);
            }
        }

        return results;
    }
}
