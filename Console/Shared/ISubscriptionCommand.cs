using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using DotMake.CommandLine;

namespace Console.Shared;

public interface ISubscriptionCommand
{
    /// <summary>
    /// Gets or sets the subscription ID.
    /// </summary>
    [CliOption(
        Description = """
                The subscription ID. Can be either a resource identifier (e.g., /subscriptions/{subscriptionId}),
                a plain subscription ID GUID, or a the display name of a subscription you have access to.

                Defaults to the value of environment variable AZURE_SUBSCRIPTION_ID, if neither this
                argument is specified nor the environment variable is set, then the default
                subscription is used.
            """,
        Aliases = ["-s", "--sub", "--subscription"],
        Required = false
    )]
    public string? SubscriptionId { get; set; }
}

public static class ISubscriptionCommandExtensions
{
    public static string RequireSubscriptionId(this ISubscriptionCommand self)
    {
        self.SubscriptionId ??= Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

        if (string.IsNullOrWhiteSpace(self.SubscriptionId))
        {
            throw new InvocationException("--subscription-id is required.");
        }

        return self.SubscriptionId;
    }

    public static async Task<SubscriptionResource> GetSubscriptionResourceAsync(
        this ISubscriptionCommand self,
        ArmClient armClient,
        bool allowDisplayName = true
    )
    {
        var requestedSubscriptionId =
            self.SubscriptionId ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
        if (requestedSubscriptionId is null)
        {
            return await armClient.GetDefaultSubscriptionAsync();
        }
        else if (requestedSubscriptionId.StartsWith("/subscriptions/"))
        {
            return armClient.GetSubscriptionResource(
                new ResourceIdentifier(requestedSubscriptionId)
            );
        }
        else if (Guid.TryParse(requestedSubscriptionId, out var subscriptionId))
        {
            return armClient.GetSubscriptionResource(
                SubscriptionResource.CreateResourceIdentifier(subscriptionId.ToString())
            );
        }
        else
        {
            if (!allowDisplayName)
            {
                throw new InvocationException(
                    $"Invalid --subscription format: {self.SubscriptionId}. Expected a GUID or ResourceIdentifier."
                );
            }
            else
            {
                await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync())
                {
                    if (
                        subscription.Data.DisplayName.Equals(
                            requestedSubscriptionId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        self.SubscriptionId = subscription.Data.SubscriptionId;
                        return subscription;
                    }
                }

                throw new InvocationException(
                    $"Subscription with display name '{requestedSubscriptionId}' not found."
                );
            }
        }
    }
}
