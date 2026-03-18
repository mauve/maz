using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Console.Config;

namespace Console.Cli.Shared;

public partial class ResourceGroupOptionPack : OptionPack
{
    public readonly SubscriptionOptionPack Subscription = new();

    /// <summary>
    /// The name of the resource group.
    /// Defaults to AZURE_RESOURCE_GROUP.
    /// </summary>
    [CliOption(
        "--resource-group",
        "-g",
        CompletionProviderType = typeof(ResourceGroupCompletionProvider),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ResourceGroupName { get; }

    public override string HelpTitle => "Resource Group";

    /// <summary>
    /// Returns the effective resource group value together with its source.
    /// Checks (in order): CLI option → AZURE_RESOURCE_GROUP env var → config DefaultResourceGroup.
    /// </summary>
    public (string? Value, ValueSource Source) GetWithSource()
    {
        if (ResourceGroupName is not null)
            return (ResourceGroupName, ValueSource.Cli);

        var envVal = Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");
        if (envVal is not null)
            return (envVal, ValueSource.Environment);

        var configVal = MazConfig.Current.DefaultResourceGroup;
        if (configVal is not null)
            return (configVal, ValueSource.Config);

        return (null, ValueSource.Config);
    }

    public string RequireResourceGroupName()
    {
        var (value, _) = GetWithSource();
        var name = value;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvocationException("--resource-group is required.");

        var normalized = NormalizeRgName(name);

        if (IsDisallowedResourceGroup(normalized))
            throw new InvocationException(
                $"Resource group '{normalized}' is not allowed by the maz configuration."
            );

        return normalized;
    }

    internal static bool IsDisallowedResourceGroup(string resourceGroupName)
    {
        var config = MazConfig.Current;
        if (config.DisallowedResourceGroups.Count == 0)
            return false;

        return config.DisallowedResourceGroups.Any(dg =>
            dg.Equals(resourceGroupName, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string NormalizeRgName(string name) =>
        name.StartsWith("/rg/", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    public Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient) =>
        Subscription.GetSubscriptionAsync(armClient);
}

/// <summary>
/// Tab-completion provider for --resource-group.
/// Scopes results to the subscription specified via --subscription-id when available,
/// or to CFG1 subscriptions when configured.
/// </summary>
internal sealed class ResourceGroupCompletionProvider : ICliCompletionProvider
{
    public async ValueTask<IEnumerable<string>> GetCompletionsAsync(CliCompletionContext context)
    {
        var auth = context.GetOptionPack<AuthOptionPack>();
        var credential = auth?.GetCredential(DiagnosticLog.Null) ?? new DefaultAzureCredential();
        var armClient = new ArmClient(credential);
        var config = MazConfig.Current;

        string? subHint = context.GetOptionPack<SubscriptionOptionPack>()?.SubscriptionId;

        try
        {
            if (subHint is not null)
            {
                // Explicit --subscription-id: list RGs in that subscription.
                var sub = await SubscriptionOptionPack.ResolveAsync(armClient, subHint);
                var results = new List<string>();
                await foreach (var rg in sub.GetResourceGroups().GetAllAsync())
                    results.Add(rg.Data.Name);
                return results;
            }

            if (config.ResolutionFilter.Count > 0)
            {
                // CFG1: list RGs from configured subscriptions.
                var results = new List<string>();
                foreach (var entry in config.ResolutionFilter)
                {
                    if (entry.ResourceGroups.Count > 0)
                    {
                        results.AddRange(entry.ResourceGroups);
                    }
                    else
                    {
                        var sub = await SubscriptionOptionPack.ResolveAsync(
                            armClient,
                            entry.SubscriptionId
                        );
                        await foreach (var rg in sub.GetResourceGroups().GetAllAsync())
                            results.Add(rg.Data.Name);
                    }
                }
                return results.Distinct(StringComparer.OrdinalIgnoreCase);
            }

            return [];
        }
        catch
        {
            return [];
        }
    }
}
