using System.Text.Json;

namespace Console.Tui;

/// <summary>Synchronous tab-completion for JMESPath expressions.</summary>
internal static class JmesPathAutocomplete
{
    private static readonly string[] BuiltInFunctions =
    [
        "abs",
        "avg",
        "ceil",
        "contains",
        "ends_with",
        "floor",
        "group_by",
        "join",
        "keys",
        "length",
        "map",
        "max",
        "max_by",
        "merge",
        "min",
        "min_by",
        "not_null",
        "reverse",
        "sort",
        "sort_by",
        "starts_with",
        "sum",
        "to_array",
        "to_number",
        "to_string",
        "type",
        "values",
    ];

    private static readonly CompletionItem[] PatternCompletions =
    [
        new("[*]", "wildcard"),
        new("[?", "filter"),
        new("[]", "flatten"),
        new("| ", "pipe"),
    ];

    public static List<CompletionItem> GetCompletions(string currentWord, JsonElement inputJson)
    {
        var candidates = new List<CompletionItem>();

        // Property names from the input JSON
        foreach (var name in EnumeratePropertyNames(inputJson))
            candidates.Add(new CompletionItem(name, "property"));

        // Built-in functions
        foreach (var fn in BuiltInFunctions)
            candidates.Add(new CompletionItem(fn, "function"));

        // Common patterns
        candidates.AddRange(PatternCompletions);

        // Empty prefix: show all property names first, then functions
        if (string.IsNullOrEmpty(currentWord))
        {
            return
            [
                .. candidates
                    .DistinctBy(c => c.InsertText, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c.TypeLabel == "property" ? 0 : c.TypeLabel == "function" ? 1 : 2)
                    .ThenBy(c => c.InsertText, StringComparer.OrdinalIgnoreCase)
                    .Take(20),
            ];
        }

        return
        [
            .. candidates
                .Where(c =>
                    c.InsertText.Length > currentWord.Length
                    && (
                        c.InsertText.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)
                        || FuzzyMatch.IsSubsequenceMatch(c.InsertText, currentWord)
                    )
                )
                .DistinctBy(c => c.InsertText, StringComparer.OrdinalIgnoreCase)
                .OrderBy(c =>
                    c.InsertText.StartsWith(currentWord, StringComparison.Ordinal)
                        ? 0
                    : c.InsertText.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)
                        ? 1
                    : 2
                )
                .ThenBy(c => c.InsertText, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .Select(c => c with
                {
                    MatchIndices = FuzzyMatch.ComputeMatchIndices(c.InsertText, currentWord),
                }),
        ];
    }

    /// <summary>Walks the JSON structure to collect all property names (deduplicated).</summary>
    private static HashSet<string> EnumeratePropertyNames(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(element, names, depth: 0);
        return names;
    }

    private static void CollectNames(JsonElement element, HashSet<string> names, int depth)
    {
        if (depth > 5)
            return; // avoid excessive recursion

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    names.Add(prop.Name);
                    CollectNames(prop.Value, names, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectNames(item, names, depth + 1);
                    break; // sample only first element
                }
                break;
        }
    }
}
