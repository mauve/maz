using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using System.CommandLine;

namespace Console.Cli.Shared;

public class SubscriptionOptionPack : OptionPack
{
    public readonly Option<string?> SubscriptionId;

    public SubscriptionOptionPack()
    {
        SubscriptionId = new Option<string?>(
            "--subscription-id",
            ["-s", "--sub", "--subscription"]
        )
        {
            Description =
                """
            The subscription ID. Can be a resource identifier (/subscriptions/{id}),
            a plain GUID, or the display name of a subscription you have access to.
            Defaults to AZURE_SUBSCRIPTION_ID, or the default subscription if unset.
            """
        };
    }

    internal override void AddOptionsTo(Command cmd) => cmd.Add(SubscriptionId);

    public async Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient)
    {
        var requested = GetValue(SubscriptionId) ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

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
