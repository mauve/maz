using System.Text;
using System.Text.RegularExpressions;

namespace SpecGenerator.Modeling;

/// <summary>
/// Converts OpenAPI operationIds and schema names into idiomatic CLI names and C# class names.
/// </summary>
public static partial class NamingEngine
{
    // Irregulars that the simple algorithm would mangle
    private static readonly Dictionary<string, string> _singularTable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["accounts"] = "account",
            ["addresses"] = "address",
            ["aliases"] = "alias",
            ["blobs"] = "blob",
            ["capacities"] = "capacity",
            ["certificates"] = "certificate",
            ["clusters"] = "cluster",
            ["containers"] = "container",
            ["databases"] = "database",
            ["endpoints"] = "endpoint",
            ["entries"] = "entry",
            ["environments"] = "environment",
            ["files"] = "file",
            ["gateways"] = "gateway",
            ["groups"] = "group",
            ["identities"] = "identity",
            ["instances"] = "instance",
            ["items"] = "item",
            ["jobs"] = "job",
            ["keys"] = "key",
            ["locations"] = "location",
            ["logs"] = "log",
            ["namespaces"] = "namespace",
            ["networks"] = "network",
            ["nodes"] = "node",
            ["objects"] = "object",
            ["operations"] = "operation",
            ["policies"] = "policy",
            ["properties"] = "property",
            ["queues"] = "queue",
            ["registries"] = "registry",
            ["resources"] = "resource",
            ["results"] = "result",
            ["roles"] = "role",
            ["rules"] = "rule",
            ["secrets"] = "secret",
            ["services"] = "service",
            ["shares"] = "share",
            ["snapshots"] = "snapshot",
            ["spaces"] = "space",
            ["storageaccounts"] = "storageaccount",
            ["subscriptions"] = "subscription",
            ["tables"] = "table",
            ["tags"] = "tag",
            ["tasks"] = "task",
            ["tenants"] = "tenant",
            ["topics"] = "topic",
            ["types"] = "type",
            ["usages"] = "usage",
            ["vaults"] = "vault",
            ["versions"] = "version",
            ["volumes"] = "volume",
            ["zones"] = "zone",
        };

    /// <summary>
    /// Converts an operationId like "StorageAccounts_Create" for service "storage" into:
    /// resource = "account", action = "create".
    /// </summary>
    public static (string Resource, string Action) SplitOperationId(
        string operationId,
        string serviceDisplayName)
    {
        var underscoreIdx = operationId.IndexOf('_', StringComparison.Ordinal);
        string prefix, suffix;

        if (underscoreIdx < 0)
        {
            prefix = operationId;
            suffix = "show";
        }
        else
        {
            prefix = operationId[..underscoreIdx];
            suffix = operationId[(underscoreIdx + 1)..];
        }

        // Strip leading service name from prefix (case-insensitive word boundary)
        prefix = StripServicePrefix(prefix, serviceDisplayName);

        // Singularize the remaining prefix (the resource name)
        var resource = Singularize(prefix);

        // Convert to kebab-case
        var resourceCli = PascalToKebab(resource);
        var actionCli = PascalToKebab(suffix);

        return (resourceCli, actionCli);
    }

    /// <summary>
    /// Converts a resource name (kebab or pascal) and action name into a C# class name.
    /// e.g. resource="account", action="create", service="Storage" → "StorageAccountCreateCommandDef"
    /// </summary>
    public static string ToClassName(string serviceClassName, string resourceCli, string actionCli)
    {
        var resource = KebabToPascal(resourceCli);
        var action = KebabToPascal(actionCli);
        return $"{serviceClassName}{resource}{action}CommandDef";
    }

    /// <summary>Converts a C# class base name (e.g. "StorageAccount") to a kebab CLI name.</summary>
    public static string PascalToKebab(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
            return pascalCase;

        var sb = new StringBuilder();
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('-');
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Converts a kebab-case string to PascalCase.</summary>
    public static string KebabToPascal(string kebab)
    {
        if (string.IsNullOrEmpty(kebab))
            return kebab;

        var parts = kebab.Split('-');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>Converts a camelCase or snake_case property name to a kebab CLI flag.</summary>
    public static string PropertyToKebab(string name)
    {
        // Strip leading $ (OData query parameters like $filter, $maxpagesize)
        if (name.StartsWith('$'))
            name = name[1..];

        if (string.IsNullOrEmpty(name))
            return name;

        // Handle camelCase → kebab-case
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_' || c == '-')
            {
                sb.Append('-');
                continue;
            }

            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1]))
                sb.Append('-');

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Converts a kebab-case property name to a C# PascalCase property name.</summary>
    public static string KebabToCSharpProperty(string kebab)
    {
        return KebabToPascal(kebab.Replace("--", "-"));
    }

    private static string StripServicePrefix(string prefix, string serviceDisplayName)
    {
        if (string.IsNullOrEmpty(serviceDisplayName))
            return prefix;

        // Try stripping the service name word-by-word from the start of prefix
        // e.g. prefix="StorageAccounts", service="storage" → "Accounts"
        var serviceParts = serviceDisplayName
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant())
            .ToList();

        var prefixLower = prefix.ToLowerInvariant();

        foreach (var part in serviceParts)
        {
            if (prefixLower.StartsWith(part, StringComparison.OrdinalIgnoreCase)
                && prefix.Length > part.Length)
            {
                prefix = prefix[part.Length..];
                prefixLower = prefix.ToLowerInvariant();
            }
        }

        return prefix;
    }

    private static string Singularize(string word)
    {
        if (string.IsNullOrEmpty(word))
            return word;

        var lower = word.ToLowerInvariant();

        if (_singularTable.TryGetValue(lower, out var singular))
            return PreserveCase(word, singular);

        // Simple fallback: strip trailing 's' if safe
        if (lower.EndsWith("ies", StringComparison.Ordinal) && lower.Length > 4)
        {
            // entries → entry, policies → policy
            return PreserveCase(word, lower[..^3] + "y");
        }

        if (lower.EndsWith("ses", StringComparison.Ordinal)
            || lower.EndsWith("xes", StringComparison.Ordinal)
            || lower.EndsWith("zes", StringComparison.Ordinal)
            || lower.EndsWith("ches", StringComparison.Ordinal)
            || lower.EndsWith("shes", StringComparison.Ordinal))
        {
            return PreserveCase(word, lower[..^2]); // strip "es"
        }

        if (lower.EndsWith('s') && !lower.EndsWith("ss") && lower.Length >= 4)
            return PreserveCase(word, lower[..^1]);

        return word;
    }

    private static string PreserveCase(string original, string lower)
    {
        if (original.Length == 0)
            return lower;

        // If original is PascalCase/CamelCase, capitalize the result
        if (char.IsUpper(original[0]))
            return char.ToUpperInvariant(lower[0]) + lower[1..];

        return lower;
    }

    [GeneratedRegex(@"[A-Za-z][a-z0-9]*")]
    private static partial Regex WordsRegex();
}
