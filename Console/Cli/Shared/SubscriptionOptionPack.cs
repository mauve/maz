using Azure.Identity;
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
    [CliOption(
        "--subscription-id",
        "-s",
        EnvVar = "AZURE_SUBSCRIPTION_ID",
        CompletionProviderType = typeof(SubscriptionIdCompletionProvider),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? SubscriptionId { get; }

    public override string HelpTitle => "Subscription";

    public string RequireSubscriptionId()
    {
        var id = SubscriptionId ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvocationException("--subscription-id is required.");

        if (id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Split('/');
            return parts.Length > 2 ? parts[2] : id;
        }

        return id;
    }

    public async Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient)
    {
        var requested =
            SubscriptionId ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");

        if (requested is null)
            return await armClient.GetDefaultSubscriptionAsync();

        if (requested.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            return armClient.GetSubscriptionResource(new(requested));

        if (Guid.TryParse(requested, out var guid))
            return armClient.GetSubscriptionResource(
                Azure.ResourceManager.Resources.SubscriptionResource.CreateResourceIdentifier(
                    guid.ToString()
                )
            );

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            if (sub.Data.DisplayName.Equals(requested, StringComparison.OrdinalIgnoreCase))
                return sub;
        }

        throw new InvocationException($"Subscription with display name '{requested}' not found.");
    }
}

internal sealed class SubscriptionIdCompletionProvider : ICliCompletionProvider
{
    public async ValueTask<IEnumerable<string>> GetCompletionsAsync(CliCompletionContext context)
    {
        var auth = context.GetOptionPack<AuthOptionPack>();
        var credential = auth?.GetCredential() ?? new DefaultAzureCredential();
        var armClient = new ArmClient(credential);
        var word = context.WordToComplete;
        var suggestions = new List<string>();

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            var id = sub.Data.SubscriptionId;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (
                word.Length > 0
                && !id.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                && !(
                    sub.Data.DisplayName?.Contains(word, StringComparison.OrdinalIgnoreCase)
                    ?? false
                )
            )
                continue;

            suggestions.Add(id);
        }

        return suggestions;
    }
}
