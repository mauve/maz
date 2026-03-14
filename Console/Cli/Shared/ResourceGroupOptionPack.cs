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
    [CliOption("--resource-group", "-g")]
    public partial string? ResourceGroupName { get; }

    public override string HelpTitle => "Resource Group";

    public string RequireResourceGroupName()
    {
        var name = ResourceGroupName ?? Environment.GetEnvironmentVariable("AZURE_RESOURCE_GROUP");
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
