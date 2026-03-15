namespace Console.Tui;

/// <summary>Provides KQL tab-completions from keyword list and workspace schema.</summary>
internal static class KqlAutocomplete
{
    private static readonly string[] KqlKeywords =
    [
        "where", "summarize", "by", "extend", "project", "distinct", "top", "limit", "take",
        "join", "union", "let", "as", "render", "make-series", "mv-expand", "parse", "range",
        "sort", "order", "search", "print", "getschema", "evaluate", "datatable",
        "count", "sum", "avg", "min", "max", "countif", "sumif", "dcount", "dcountif",
        "tostring", "toint", "tolong", "todouble", "todatetime", "totimespan",
        "format_datetime", "format_timespan", "now", "ago", "bin", "floor", "ceiling", "round",
        "strlen", "substring", "split", "strcat", "toupper", "tolower", "trim", "replace",
        "parse_json", "todynamic", "tobool", "iff", "case", "coalesce",
        "isempty", "isnotempty", "isnull", "isnotnull", "true", "false",
        "make_list", "make_set", "percentile", "stdev", "any",
        "startofday", "endofday", "startofmonth", "endofmonth",
        "array_length", "bag_keys", "strcat_array", "pack_all", "pack",
        "and", "or", "not", "asc", "desc", "kind", "on", "with",
        "project-away", "project-rename", "project-reorder", "project-keep", "project-smart",
        "mv-apply", "make-series",
    ];

    public static async Task<List<string>> GetCompletionsAsync(
        string prefix,
        string fullQuery,
        SchemaProvider schema,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(prefix))
            return [];

        // Determine context: are we in the first segment (before any pipe) or after one?
        // First segment → suggest table names (user is typing the source table).
        // After a pipe   → suggest column names (user is filtering/projecting).
        // Table names are never useful after a pipe; column names are not useful before one.
        bool afterPipe = QueryHasPipe(fullQuery);

        IEnumerable<string> schemaCompletions;
        if (afterPipe)
        {
            var tables = await schema.GetTablesAsync(ct);
            var firstTable = FindFirstTable(fullQuery, tables);
            var columns = firstTable is not null
                ? await schema.GetColumnsAsync(firstTable, ct)
                : [];
            schemaCompletions = columns;
        }
        else
        {
            schemaCompletions = await schema.GetTablesAsync(ct);
        }

        return [.. KqlKeywords
            .Concat(schemaCompletions)
            .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && c.Length > prefix.Length)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Take(20)];
    }

    /// <summary>
    /// Returns true if the query contains at least one unquoted pipe operator,
    /// meaning the cursor is (or will be) in a post-source pipe segment.
    /// </summary>
    internal static bool QueryHasPipe(string query)
    {
        bool inString = false;
        char stringChar = '"';
        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == stringChar) inString = false;
                continue;
            }
            if (c == '"' || c == '\'') { inString = true; stringChar = c; continue; }
            if (c == '/' && i + 1 < query.Length && query[i + 1] == '/') break;
            if (c == '|') return true;
        }
        return false;
    }

    private static string? FindFirstTable(string query, IReadOnlyList<string> knownTables)
    {
        foreach (var rawLine in query.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith('|'))
                continue;
            int end = 0;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
                end++;
            if (end == 0) continue;
            var word = line[..end];
            return knownTables.FirstOrDefault(t =>
                string.Equals(t, word, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }
}
