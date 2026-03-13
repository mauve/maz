using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

namespace Console.Cli.Shared;

public partial class SubscriptionOptionPack : OptionPack
{
    /// <summary>
    /// The subscription ID. Can be a resource identifier (/subscriptions/{id}),
    /// a plain GUID, or the display name of a subscription you have access to.
    /// Defaults to AZURE_SUBSCRIPTION_ID, or the default subscription if unset.
    /// </summary>
    [CliOption("--subscription-id", "-s", "--sub", "--subscription")]
    public partial string? SubscriptionId { get; }

    public override string HelpTitle => "Subscription";

    public async Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient)
    {
        var requested = SubscriptionId ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

        if (requested is null)
            return await armClient.GetDefaultSubscriptionAsync();

        if (requested.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            return armClient.GetSubscriptionResource(new(requested));

        if (Guid.TryParse(requested, out var guid))
            return armClient.GetSubscriptionResource(
                Azure.ResourceManager.Resources.SubscriptionResource.CreateResourceIdentifier(guid.ToString())
            );

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            if (sub.Data.DisplayName.Equals(requested, StringComparison.OrdinalIgnoreCase))
                return sub;
        }

        throw new InvocationException($"Subscription with display name '{requested}' not found.");
    }
}
