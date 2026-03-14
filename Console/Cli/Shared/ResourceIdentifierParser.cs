namespace Console.Cli.Shared;

/// <summary>
/// The result of parsing a combined resource identifier string such as
/// "name", "rg/name", "sub/rg/name", or "/s/sub/rg/name".
/// </summary>
public sealed record ParsedResourceIdentifier(
    string? SubscriptionSegment,
    string? ResourceGroupSegment,
    string ResourceNameSegment
);

/// <summary>
/// Pure utility for parsing combined resource identifier strings.
/// No Azure SDK or OptionPack dependency.
/// </summary>
public static class ResourceIdentifierParser
{
    /// <summary>
    /// Parses a combined resource identifier string.
    ///
    /// Recognised input formats:
    ///   name              → (null,   null, name)
    ///   rg/name           → (null,   rg,   name)
    ///   sub/rg/name       → (sub,    rg,   name)
    ///   /subscriptions/{guid}/rg/name → (/subscriptions/{guid}, rg, name)
    ///   /s/{token}/rg/name            → (/s/{token},            rg, name)
    ///   /type/name        → (null,   null, name)  — leading /type/ prefix stripped
    ///
    /// Resource-group segments are normalised via
    /// <see cref="NormalizeResourceGroupSegment"/> (strips leading "/rg/").
    /// </summary>
    public static ParsedResourceIdentifier Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            throw new ArgumentException("Resource identifier cannot be empty.", nameof(raw));

        string? subscriptionSegment = null;
        string remaining = raw;

        if (raw.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            // Extract /subscriptions/{guid} up to the next slash.
            int afterPrefix = "/subscriptions/".Length;
            int nextSlash = raw.IndexOf('/', afterPrefix);
            if (nextSlash < 0)
            {
                subscriptionSegment = raw;
                remaining = "";
            }
            else
            {
                subscriptionSegment = raw[..nextSlash];
                remaining = raw[(nextSlash + 1)..];
            }
        }
        else if (raw.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            // Extract /s/{token} up to the next slash.
            int afterPrefix = "/s/".Length;
            int nextSlash = raw.IndexOf('/', afterPrefix);
            if (nextSlash < 0)
            {
                subscriptionSegment = raw;
                remaining = "";
            }
            else
            {
                subscriptionSegment = raw[..nextSlash];
                remaining = raw[(nextSlash + 1)..];
            }
        }
        else if (raw.StartsWith('/'))
        {
            // Generic leading /type/ prefix (e.g. /kv/, /rg/) — strip the prefix segment.
            int secondSlash = raw.IndexOf('/', 1);
            remaining = secondSlash >= 0 ? raw[(secondSlash + 1)..] : raw[1..];
        }

        // Positional split on the remaining string.
        if (subscriptionSegment is not null)
        {
            // Subscription already known; remaining tokens are [name] or [rg, name, …].
            if (string.IsNullOrEmpty(remaining))
                return new ParsedResourceIdentifier(subscriptionSegment, null, subscriptionSegment);

            var parts = remaining.Split('/');
            return parts.Length switch
            {
                1 => new ParsedResourceIdentifier(
                    subscriptionSegment, null, parts[0]),
                _ => new ParsedResourceIdentifier(
                    subscriptionSegment,
                    NormalizeResourceGroupSegment(parts[0]),
                    string.Join("/", parts[1..]))
            };
        }
        else
        {
            // No subscription extracted; tokens are [name], [rg, name], or [sub, rg, name, …].
            if (string.IsNullOrEmpty(remaining))
                throw new ArgumentException("Resource identifier must contain at least a resource name.", nameof(raw));

            var parts = remaining.Split('/');
            return parts.Length switch
            {
                1 => new ParsedResourceIdentifier(null, null, parts[0]),
                2 => new ParsedResourceIdentifier(
                    null,
                    NormalizeResourceGroupSegment(parts[0]),
                    parts[1]),
                _ => new ParsedResourceIdentifier(
                    parts[0],
                    NormalizeResourceGroupSegment(parts[1]),
                    string.Join("/", parts[2..]))
            };
        }
    }

    /// <summary>
    /// Normalises a subscription segment: converts "/s/{x}" → "/subscriptions/{x}".
    /// All other values (GUID, display name, "/subscriptions/…") are passed through unchanged.
    /// </summary>
    public static string? NormalizeSubscriptionSegment(string? segment)
    {
        if (segment is null) return null;
        if (segment.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
            return "/subscriptions/" + segment[3..];
        return segment;
    }

    /// <summary>
    /// Normalises a resource-group segment: strips a leading "/rg/" prefix.
    /// All other values are passed through unchanged.
    /// </summary>
    public static string? NormalizeResourceGroupSegment(string? segment)
    {
        if (segment is null) return null;
        if (segment.StartsWith("/rg/", StringComparison.OrdinalIgnoreCase))
            return segment[4..];
        return segment;
    }
}
