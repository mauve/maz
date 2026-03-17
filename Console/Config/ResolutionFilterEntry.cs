namespace Console.Config;

/// <summary>
/// A per-subscription resolution filter from <c>[resolution.*]</c> config sections (CFG1).
/// When present, resource resolution is scoped to the listed resource groups for that subscription.
/// An empty <see cref="ResourceGroups"/> list means "all resource groups in this subscription".
/// </summary>
public sealed record ResolutionFilterEntry(
    string SubscriptionId,
    IReadOnlyList<string> ResourceGroups
);
