namespace Console.Cli.Shared;

/// <summary>
/// The result of parsing a combined resource identifier string such as
/// "name", "rg/name", "sub/rg/name", or "/s/sub/rg/name".
/// </summary>
public sealed record ParsedResourceIdentifier(
    string? SubscriptionSegment,
    string? ResourceGroupSegment,
    string ResourceNameSegment,
    string? DiscardedChildPath = null
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
    ///   /subscriptions/{guid}/resourceGroups/{rg}/providers/{ns}/{type}/{name}[/child/...] → ARM resource ID
    ///   /subscriptions/{guid}/rg/name → (/subscriptions/{guid}, rg, name)
    ///   /s/{token}/rg/name            → (/s/{token},            rg, name)
    ///   https://portal.azure.com/#... → extracted ARM resource ID (R6)
    ///
    /// Empty path segments (e.g. "sub//name") are rejected with a clear error.
    /// Child resource path segments beyond {type}/{name} are captured in <see cref="ParsedResourceIdentifier.DiscardedChildPath"/>.
    /// </summary>
    public static ParsedResourceIdentifier Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            throw new ArgumentException("Resource identifier cannot be empty.", nameof(raw));

        // R6: Portal URL — preprocess to extract the embedded ARM resource ID.
        if (raw.StartsWith("https://portal.azure.com/#", StringComparison.OrdinalIgnoreCase))
            return ParsePortalUrl(raw);

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

                // Detect full ARM resource ID:
                // /subscriptions/{guid}/resourceGroups/{rg}/providers/{ns}/{type}/{name}[/child...]
                if (remaining.StartsWith("resourceGroups/", StringComparison.OrdinalIgnoreCase))
                    return ParseFullArmResourceId(subscriptionSegment, remaining);
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
            ValidateNoEmptySegments(parts, raw);
            return parts.Length switch
            {
                1 => new ParsedResourceIdentifier(subscriptionSegment, null, parts[0]),
                _ => new ParsedResourceIdentifier(
                    subscriptionSegment,
                    NormalizeResourceGroupSegment(parts[0]),
                    string.Join("/", parts[1..])
                ),
            };
        }
        else
        {
            // No subscription extracted; tokens are [name], [rg, name], or [sub, rg, name, …].
            if (string.IsNullOrEmpty(remaining))
                throw new ArgumentException(
                    "Resource identifier must contain at least a resource name.",
                    nameof(raw)
                );

            var parts = remaining.Split('/');
            ValidateNoEmptySegments(parts, raw);
            return parts.Length switch
            {
                1 => new ParsedResourceIdentifier(null, null, parts[0]),
                2 => new ParsedResourceIdentifier(
                    null,
                    NormalizeResourceGroupSegment(parts[0]),
                    parts[1]
                ),
                _ => new ParsedResourceIdentifier(
                    parts[0],
                    NormalizeResourceGroupSegment(parts[1]),
                    string.Join("/", parts[2..])
                ),
            };
        }
    }

    /// <summary>
    /// Normalises a subscription segment: converts "/s/{x}" → "/subscriptions/{x}".
    /// All other values (GUID, display name, "/subscriptions/…") are passed through unchanged.
    /// </summary>
    public static string? NormalizeSubscriptionSegment(string? segment)
    {
        if (segment is null)
            return null;
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
        if (segment is null)
            return null;
        if (segment.StartsWith("/rg/", StringComparison.OrdinalIgnoreCase))
            return segment[4..];
        return segment;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Throws if any segment in the split array is empty (catches inputs like "sub//name").
    /// </summary>
    private static void ValidateNoEmptySegments(string[] parts, string raw)
    {
        if (parts.Any(p => p.Length == 0))
            throw new ArgumentException(
                "Invalid format: empty path segment. "
                    + "The format subscriptionId//resourceName is not supported.",
                nameof(raw)
            );
    }

    /// <summary>
    /// Handles R6: Azure Portal URLs of the form
    /// https://portal.azure.com/#@tenant/resource/subscriptions/{guid}/resourceGroups/{rg}/providers/{ns}/{type}/{name}
    /// </summary>
    private static ParsedResourceIdentifier ParsePortalUrl(string portalUrl)
    {
        // Find "/resource" to locate the ARM resource ID portion.
        const string marker = "/resource";
        int markerIdx = portalUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0)
            throw new ArgumentException(
                "Could not extract an ARM resource ID from the provided portal URL.",
                nameof(portalUrl)
            );

        string armPath = portalUrl[(markerIdx + marker.Length)..];
        if (!armPath.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Could not extract an ARM resource ID from the provided portal URL.",
                nameof(portalUrl)
            );

        // Now parse as a full ARM resource ID.
        int afterSubPrefix = "/subscriptions/".Length;
        int nextSlash = armPath.IndexOf('/', afterSubPrefix);
        if (nextSlash < 0)
            throw new ArgumentException(
                "Could not extract an ARM resource ID from the provided portal URL.",
                nameof(portalUrl)
            );

        var subscriptionSegment = armPath[..nextSlash];
        var remaining = armPath[(nextSlash + 1)..];

        if (!remaining.StartsWith("resourceGroups/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Could not extract an ARM resource ID from the provided portal URL.",
                nameof(portalUrl)
            );

        return ParseFullArmResourceId(subscriptionSegment, remaining);
    }

    /// <summary>
    /// Parses the portion after "/subscriptions/{guid}/" when it starts with "resourceGroups/".
    /// Expected format: resourceGroups/{rg}/providers/{ns}/{type}/{name}[/childType/childName/...]
    /// </summary>
    private static ParsedResourceIdentifier ParseFullArmResourceId(
        string subscriptionSegment,
        string remaining
    )
    {
        // remaining = "resourceGroups/{rg}/providers/{ns}/{type}/{name}[/...]"
        var parts = remaining.Split('/');
        // parts[0] = "resourceGroups"
        // parts[1] = "{rg}"
        // parts[2] = "providers"
        // parts[3] = "{ns}"
        // parts[4] = "{type}"
        // parts[5] = "{name}"
        // parts[6..] = child resource segments (optional)

        if (
            parts.Length < 6
            || !parts[0].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("providers", StringComparison.OrdinalIgnoreCase)
        )
        {
            // Not a full ARM resource ID — fall through to positional parse
            ValidateNoEmptySegments(parts, remaining);
            return parts.Length switch
            {
                1 => new ParsedResourceIdentifier(subscriptionSegment, null, parts[0]),
                _ => new ParsedResourceIdentifier(
                    subscriptionSegment,
                    NormalizeResourceGroupSegment(parts[0]),
                    string.Join("/", parts[1..])
                ),
            };
        }

        var rg = parts[1];
        var name = parts[5];
        string? childPath = parts.Length > 6 ? string.Join("/", parts[6..]) : null;

        return new ParsedResourceIdentifier(subscriptionSegment, rg, name, childPath);
    }
}
