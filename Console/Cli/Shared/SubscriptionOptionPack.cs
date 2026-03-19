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

        return ExtractSubscriptionGuid(id)
            ?? throw new InvocationException(
                $"Cannot resolve subscription '{id}' without an ARM client. "
                    + "Use a GUID, /subscriptions/{{guid}}, or /s/name:guid format."
            );
    }

    /// <summary>
    /// Async version of <see cref="RequireSubscriptionId"/> that can resolve display-name
    /// shorthands (e.g. /s/prod) via an ARM client call.
    /// </summary>
    public async Task<string> RequireSubscriptionIdAsync(ArmClient armClient)
    {
        var (value, _) = GetWithSource();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvocationException("--subscription-id is required.");

        // Fast path: formats that can be resolved without an ARM call
        var quick = ExtractSubscriptionGuid(value);
        if (quick is not null)
            return quick;

        // Slow path: resolve display name via ARM
        var sub = await ResolveAsync(armClient, value);
        var subId = sub.Id.Name;

        if (IsDisallowedSubscriptionId(subId))
            throw new InvocationException(
                $"Subscription '{subId}' is not allowed by the maz configuration."
            );

        return subId;
    }

    /// <summary>
    /// Extracts a plain subscription GUID from the known synchronous formats.
    /// Returns null when async resolution (display-name lookup) is required.
    /// </summary>
    private static string? ExtractSubscriptionGuid(string hint)
    {
        if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = hint.Split('/');
            return parts.Length > 2 ? parts[2] : null;
        }

        if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            var token = hint[3..];
            var colonIdx = token.IndexOf(':');
            if (colonIdx >= 0)
                return token[(colonIdx + 1)..]; // /s/name:guid → guid
            if (Guid.TryParse(token, out _))
                return token; // /s/guid → guid
            return null; // /s/displayName → needs async resolution
        }

        if (Guid.TryParse(hint, out var guid))
            return guid.ToString();

        return null; // plain display name → needs async resolution
    }

    /// <summary>
    /// Returns the effective subscription value together with its source.
    /// Checks (in order): CLI option → AZURE_SUBSCRIPTION_ID env var → config DefaultSubscriptionId.
    /// </summary>
    public (string? Value, ValueSource Source) GetWithSource()
    {
        if (_opt_SubscriptionId.WasProvided && SubscriptionId is not null)
            return (SubscriptionId, ValueSource.Cli);

        var envVal = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
        if (envVal is not null)
            return (envVal, ValueSource.Environment);

        var configVal = MazConfig.Current.DefaultSubscriptionId;
        if (configVal is not null)
            return (configVal, ValueSource.Config);

        return (null, ValueSource.Config);
    }

    public async Task<SubscriptionResource> GetSubscriptionAsync(ArmClient armClient)
    {
        var (value, _) = GetWithSource();
        var result = await ResolveAsync(armClient, value);

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
    /// For "/s/{x}", if x contains no colon and is not a GUID, it is treated as a display name.
    /// </summary>
    internal static async Task<SubscriptionResource> ResolveAsync(ArmClient armClient, string? hint)
    {
        if (hint is null)
            return await armClient.GetDefaultSubscriptionAsync();

        if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            return armClient.GetSubscriptionResource(new(hint));

        if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            var token = hint[3..];
            var colonIdx = token.IndexOf(':');
            if (colonIdx >= 0)
            {
                // /s/name:guid  → use the guid
                var id = token[(colonIdx + 1)..];
                return armClient.GetSubscriptionResource(
                    new ResourceIdentifier("/subscriptions/" + id)
                );
            }
            else if (Guid.TryParse(token, out _))
            {
                // /s/guid  → use as-is
                return armClient.GetSubscriptionResource(
                    new ResourceIdentifier("/subscriptions/" + token)
                );
            }
            else
            {
                // /s/displayName → resolve by display name (GAP-10)
                await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
                {
                    if (sub.Data.DisplayName.Equals(token, StringComparison.OrdinalIgnoreCase))
                        return sub;
                }
                throw new InvocationException(
                    $"Subscription with display name '{token}' not found."
                );
            }
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
        var credential = auth?.GetCredential(DiagnosticLog.Null) ?? new DefaultAzureCredential();
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
                && !config.AllowedSubscriptions.Any(h =>
                    SubscriptionOptionPack.SubscriptionMatchesHint(sub.Data, h)
                )
            )
                continue;

            // Apply deny-list filter
            if (
                config.DisallowedSubscriptions.Any(h =>
                    SubscriptionOptionPack.SubscriptionMatchesHint(sub.Data, h)
                )
            )
                continue;

            var name = (sub.Data.DisplayName ?? "").Replace("/", "-"); // GAP-14: strip '/' from display name
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
