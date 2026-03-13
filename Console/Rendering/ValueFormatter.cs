using System.Collections;
using System.Text.RegularExpressions;

namespace Console.Rendering;

public enum TextAlignment { Left, Right, Center }

public record FormattedValue(string Text, TextAlignment Alignment, string? AnsiCode);

public record ValueFormatterOptions(string DateFormat = "yyyy-MM-ddTHH:mm:ssZ");

public static partial class ValueFormatter
{
    private static readonly HashSet<string> SucceededStates = new(StringComparer.OrdinalIgnoreCase)
        { "Succeeded" };
    private static readonly HashSet<string> FailedStates = new(StringComparer.OrdinalIgnoreCase)
        { "Failed", "Canceled" };
    private static readonly HashSet<string> RunningStates = new(StringComparer.OrdinalIgnoreCase)
        { "Running", "Creating", "Updating" };
    private static readonly HashSet<string> DeletingStates = new(StringComparer.OrdinalIgnoreCase)
        { "Deleting" };

    // Match /subscriptions/{guid}
    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F\-]{36}", RegexOptions.Compiled)]
    private static partial Regex SubscriptionPrefixRegex();

    // Match /resourceGroups/NAME/ or /resourceGroups/NAME (end)
    [GeneratedRegex(@"/resourceGroups/([^/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ResourceGroupRegex();

    public static FormattedValue Format(object? value, ValueFormatterOptions options)
    {
        if (value is null)
            return new FormattedValue("null", TextAlignment.Left, "\x1b[2;35m"); // dim magenta

        if (value is bool b)
            return b
                ? new FormattedValue("✓", TextAlignment.Center, "\x1b[32m")  // green
                : new FormattedValue("✗", TextAlignment.Center, "\x1b[31m"); // red

        if (value is int or long or decimal or double or float or short or byte or uint or ulong)
            return new FormattedValue(value.ToString()!, TextAlignment.Right, null);

        if (value is DateTime dt)
            return new FormattedValue(dt.ToString(options.DateFormat), TextAlignment.Left, null);

        if (value is DateTimeOffset dto)
            return new FormattedValue(dto.ToString(options.DateFormat), TextAlignment.Left, null);

        if (value is Guid g)
            return new FormattedValue(g.ToString(), TextAlignment.Left, "\x1b[2m"); // dim

        if (value is string s)
            return FormatString(s, options);

        if (value is IDictionary dict)
            return FormatDictionary(dict);

        if (value is IEnumerable enumerable)
            return FormatEnumerable(enumerable);

        // enum or fallback
        return new FormattedValue(value.ToString() ?? "", TextAlignment.Left, null);
    }

    private static FormattedValue FormatString(string s, ValueFormatterOptions options)
    {
        if (SucceededStates.Contains(s))
            return new FormattedValue("✓ " + s, TextAlignment.Left, "\x1b[32m");
        if (FailedStates.Contains(s))
            return new FormattedValue("✗ " + s, TextAlignment.Left, "\x1b[31m");
        if (RunningStates.Contains(s))
            return new FormattedValue("⟳ " + s, TextAlignment.Left, "\x1b[33m");
        if (DeletingStates.Contains(s))
            return new FormattedValue("~ " + s, TextAlignment.Left, "\x1b[2m");

        if (s.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            return FormatResourceId(s);

        return new FormattedValue(s, TextAlignment.Left, null);
    }

    private static FormattedValue FormatResourceId(string id)
    {
        // Strip /subscriptions/{guid}
        var stripped = SubscriptionPrefixRegex().Replace(id, "");
        // Replace /resourceGroups/NAME with rg:NAME
        stripped = ResourceGroupRegex().Replace(stripped, m => $"/rg:{m.Groups[1].Value}");
        // Remove leading slash if present
        if (stripped.StartsWith('/'))
            stripped = stripped[1..];

        return new FormattedValue(stripped, TextAlignment.Left, "\x1b[2m");
    }

    private static FormattedValue FormatDictionary(IDictionary dict)
    {
        var count = dict.Count;
        if (count == 0)
            return new FormattedValue("0", TextAlignment.Left, null);

        var first = dict.Keys.Cast<object>().First();
        var firstVal = dict[first];
        var preview = $"{first}={firstVal}";
        var text = count == 1 ? $"1 ({preview})" : $"{count} ({preview}, …)";
        return new FormattedValue(text, TextAlignment.Left, null);
    }

    private static FormattedValue FormatEnumerable(IEnumerable enumerable)
    {
        var items = enumerable.Cast<object>().ToList();
        var count = items.Count;
        if (count == 0)
            return new FormattedValue("0", TextAlignment.Left, null);

        var preview = items[0]?.ToString() ?? "null";
        var text = count == 1 ? $"1 ({preview})" : $"{count} ({preview}, …)";
        return new FormattedValue(text, TextAlignment.Left, null);
    }

    /// <summary>Mid-truncates a string to fit within maxWidth, using '…' as the ellipsis.</summary>
    public static string Truncate(string text, int maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (text.Length <= maxWidth) return text;
        if (maxWidth <= 1) return "…";
        if (maxWidth == 2) return text[0] + "…";

        var half = (maxWidth - 1) / 2;
        var end = maxWidth - half - 1;
        return text[..half] + "…" + text[^end..];
    }
}
