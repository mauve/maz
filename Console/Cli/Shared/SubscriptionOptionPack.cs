using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Console.Config;

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
        var result = await ResolveAsync(
            armClient,
            SubscriptionId ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID")
        );

        var subId = result.Id.Name;
        if (IsDisallowedSubscriptionId(subId))
            throw new InvocationException(
                $"Subscription '{subId}' is not allowed by the maz configuration."
            );

        return result;
    }

    /// <summary>
    /// Resolves a subscription from an arbitrary hint string (or null for the default subscription).
    /// Accepts: null → default, "/subscriptions/{guid}", "/s/{x}", plain GUID, or display name.
    /// </summary>
    internal static async Task<SubscriptionResource> ResolveAsync(ArmClient armClient, string? hint)
    {
        if (hint is null)
            return await armClient.GetDefaultSubscriptionAsync();

        if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            return armClient.GetSubscriptionResource(new(hint));

        if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            // /s/name:guid  → use the guid, ignore the human-readable name prefix
            // /s/guid       → use as-is
            var token = hint[3..];
            var colonIdx = token.IndexOf(':');
            var id = colonIdx >= 0 ? token[(colonIdx + 1)..] : token;
            return armClient.GetSubscriptionResource(
                new ResourceIdentifier("/subscriptions/" + id)
            );
        }

        if (Guid.TryParse(hint, out var guid))
            return armClient.GetSubscriptionResource(
                SubscriptionResource.CreateResourceIdentifier(guid.ToString())
            );

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            if (sub.Data.DisplayName.Equals(hint, StringComparison.OrdinalIgnoreCase))
                return sub;
        }

        throw new InvocationException($"Subscription with display name '{hint}' not found.");
    }

    /// <summary>
    /// Returns true if the given subscription data matches a hint from an allow/disallow list.
    /// Supports /subscriptions/{guid}, /s/name:guid, plain GUID, and display name formats.
    /// </summary>
    internal static bool SubscriptionMatchesHint(SubscriptionData data, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return false;

        var id = data.SubscriptionId ?? "";
        var name = data.DisplayName ?? "";

        // Plain GUID match
        if (hint.Equals(id, StringComparison.OrdinalIgnoreCase))
            return true;

        // Display name match
        if (hint.Equals(name, StringComparison.OrdinalIgnoreCase))
            return true;

        // /subscriptions/{guid} format
        if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            var hintId = hint["/subscriptions/".Length..].TrimEnd('/');
            return hintId.Equals(id, StringComparison.OrdinalIgnoreCase);
        }

        // /s/name:guid or /s/guid format
        if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            var token = hint[3..];
            var colonIdx = token.IndexOf(':');
            if (colonIdx >= 0)
            {
                var hintName = token[..colonIdx];
                var hintId = token[(colonIdx + 1)..];
                return hintId.Equals(id, StringComparison.OrdinalIgnoreCase)
                    || hintName.Equals(name, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                return token.Equals(id, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool IsDisallowedSubscriptionId(string subId)
    {
        var config = MazConfig.Current;
        if (config.DisallowedSubscriptions.Count == 0)
            return false;

        return config.DisallowedSubscriptions.Any(hint =>
        {
            if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
                return hint["/subscriptions/".Length..]
                    .TrimEnd('/')
                    .Equals(subId, StringComparison.OrdinalIgnoreCase);

            if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
            {
                var token = hint[3..];
                var colonIdx = token.IndexOf(':');
                var hintId = colonIdx >= 0 ? token[(colonIdx + 1)..] : token;
                return hintId.Equals(subId, StringComparison.OrdinalIgnoreCase);
            }

            return hint.Equals(subId, StringComparison.OrdinalIgnoreCase);
        });
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
        var config = MazConfig.Current;

        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
        {
            var id = sub.Data.SubscriptionId;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            // Apply allow-list filter
            if (
                config.AllowedSubscriptions.Count > 0
                && !config.AllowedSubscriptions.Any(h => SubscriptionOptionPack.SubscriptionMatchesHint(sub.Data, h))
            )
                continue;

            // Apply deny-list filter
            if (config.DisallowedSubscriptions.Any(h => SubscriptionOptionPack.SubscriptionMatchesHint(sub.Data, h)))
                continue;

            var name = sub.Data.DisplayName ?? "";
            var candidate = $"/s/{name}:{id}";

            if (
                word.Length > 0
                && !candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase)
                && !name.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !id.StartsWith(word, StringComparison.OrdinalIgnoreCase)
            )
                continue;

            suggestions.Add(candidate);
        }

        return suggestions;
    }
}
