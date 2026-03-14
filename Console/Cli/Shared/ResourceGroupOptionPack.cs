using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

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
        return NormalizeRgName(name);
    }

    private static string NormalizeRgName(string name) =>
        name.StartsWith("/rg/", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;

    public Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient) =>
        Subscription.GetSubscriptionAsync(armClient);
}
