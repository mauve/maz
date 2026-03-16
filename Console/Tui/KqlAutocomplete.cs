namespace Console.Tui;

internal record ColumnInfo(string Name, string Type);

internal record CompletionItem(string InsertText, string? TypeLabel = null, int[]? MatchIndices = null)
{
    public string Display => TypeLabel is not null ? $"{InsertText} ({TypeLabel})" : InsertText;
}

/// <summary>Provides KQL tab-completions from keyword list and workspace schema.</summary>
internal static class KqlAutocomplete
{
    private static readonly string[] KqlKeywords =
    [
        "where",
        "summarize",
        "by",
        "extend",
        "project",
        "distinct",
        "top",
        "limit",
        "take",
        "join",
        "union",
        "let",
        "as",
        "render",
        "make-series",
        "mv-expand",
        "parse",
        "range",
        "sort",
        "order",
        "search",
        "print",
        "getschema",
        "evaluate",
        "datatable",
        "count",
        "sum",
        "avg",
        "min",
        "max",
        "countif",
        "sumif",
        "dcount",
        "dcountif",
        "tostring",
        "toint",
        "tolong",
        "todouble",
        "todatetime",
        "totimespan",
        "format_datetime",
        "format_timespan",
        "now",
        "ago",
        "bin",
        "floor",
        "ceiling",
        "round",
        "strlen",
        "substring",
        "split",
        "strcat",
        "toupper",
        "tolower",
        "trim",
        "replace",
        "parse_json",
        "todynamic",
        "tobool",
        "iff",
        "case",
        "coalesce",
        "isempty",
        "isnotempty",
        "isnull",
        "isnotnull",
        "true",
        "false",
        "make_list",
        "make_set",
        "percentile",
        "stdev",
        "any",
        "startofday",
        "endofday",
        "startofmonth",
        "endofmonth",
        "array_length",
        "bag_keys",
        "strcat_array",
        "pack_all",
        "pack",
        "and",
        "or",
        "not",
        "asc",
        "desc",
        "kind",
        "on",
        "with",
        "project-away",
        "project-rename",
        "project-reorder",
        "project-keep",
        "project-smart",
        "mv-apply",
        "make-series",
    ];

    public static async Task<List<CompletionItem>> GetCompletionsAsync(
        string prefix,
        string fullQuery,
        SchemaProvider schema,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(prefix))
            return [];

        // Determine context: are we in the first segment (before any pipe) or after one?
        // First segment → suggest table names (user is typing the source table).
        // After a pipe   → suggest column names (user is filtering/projecting).
        // Table names are never useful after a pipe; column names are not useful before one.
        bool afterPipe = QueryHasPipe(fullQuery);

        IEnumerable<CompletionItem> schemaCompletions;
        if (afterPipe)
        {
            var tables = await schema.GetTablesAsync(ct);
            var firstTable = FindFirstTable(fullQuery, tables);
            IReadOnlyList<ColumnInfo> columns = firstTable is not null
                ? await schema.GetColumnsAsync(firstTable, ct)
                : [];
            schemaCompletions = columns.Select(c =>
                new CompletionItem(c.Name, string.IsNullOrEmpty(c.Type) ? null : c.Type)
            );
        }
        else
        {
            var tables = await schema.GetTablesAsync(ct);
            schemaCompletions = tables.Select(t => new CompletionItem(t));
        }

        var keywordItems = KqlKeywords.Select(k => new CompletionItem(k));

        return
        [
            .. keywordItems
                .Concat(schemaCompletions)
                .Where(c =>
                    c.InsertText.Length > prefix.Length
                    && (
                        c.InsertText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || IsSubsequenceMatch(c.InsertText, prefix)
                    )
                )
                .DistinctBy(c => c.InsertText, StringComparer.OrdinalIgnoreCase)
                .OrderBy(c =>
                    // Exact-case prefix → case-insensitive prefix → subsequence
                    c.InsertText.StartsWith(prefix, StringComparison.Ordinal) ? 0
                    : c.InsertText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 1
                    : 2
                )
                .ThenBy(c => c.InsertText, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .Select(c => c with { MatchIndices = ComputeMatchIndices(c.InsertText, prefix) }),
        ];
    }

    /// <summary>
    /// Returns true if every character of <paramref name="pattern"/> appears in
    /// <paramref name="text"/> in order (case-insensitive). This is the standard
    /// fuzzy/subsequence match used by most code editors.
    /// </summary>
    internal static bool IsSubsequenceMatch(string text, string pattern)
    {
        int pi = 0;
        for (int ti = 0; ti < text.Length && pi < pattern.Length; ti++)
            if (char.ToLowerInvariant(text[ti]) == char.ToLowerInvariant(pattern[pi]))
                pi++;
        return pi == pattern.Length;
    }

    /// <summary>
    /// Returns the indices in <paramref name="text"/> that matched <paramref name="prefix"/>.
    /// For a prefix match the indices are 0..prefix.Length-1; for a subsequence match they are
    /// the positions of the matched characters (used to highlight them in the popup).
    /// </summary>
    internal static int[] ComputeMatchIndices(string text, string prefix)
    {
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return [.. Enumerable.Range(0, prefix.Length)];
        // Subsequence: record which positions in text matched
        var indices = new List<int>();
        int pi = 0;
        for (int ti = 0; ti < text.Length && pi < prefix.Length; ti++)
            if (char.ToLowerInvariant(text[ti]) == char.ToLowerInvariant(prefix[pi]))
            {
                indices.Add(ti);
                pi++;
            }
        return pi == prefix.Length ? [.. indices] : [];
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
                if (c == '\\')
                {
                    i++;
                    continue;
                }
                if (c == stringChar)
                    inString = false;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                inString = true;
                stringChar = c;
                continue;
            }
            if (c == '/' && i + 1 < query.Length && query[i + 1] == '/')
                break;
            if (c == '|')
                return true;
        }
        return false;
    }

    internal static string? FindFirstTable(string query, IReadOnlyList<string> knownTables)
    {
        foreach (var rawLine in query.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//") || line.StartsWith('|'))
                continue;
            int end = 0;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
                end++;
            if (end == 0)
                continue;
            var word = line[..end];
            return knownTables.FirstOrDefault(t =>
                string.Equals(t, word, StringComparison.OrdinalIgnoreCase)
            );
        }
        return null;
    }

    /// <summary>
    /// Returns all table names from <paramref name="knownTables"/> that appear anywhere in
    /// <paramref name="query"/> as standalone identifiers (outside string literals and comments).
    /// Handles multi-table queries: union, join, let bindings, etc.
    /// </summary>
    internal static HashSet<string> FindAllTables(string query, IReadOnlyList<string> knownTables)
    {
        if (knownTables.Count == 0)
            return [];

        var tableSet = new HashSet<string>(knownTables, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool inString = false;
        char stringChar = '"';
        int i = 0;
        while (i < query.Length)
        {
            char c = query[i];

            // Skip string literals
            if (inString)
            {
                if (c == '\\') { i += 2; continue; }
                if (c == stringChar) inString = false;
                i++;
                continue;
            }
            if (c == '"' || c == '\'') { inString = true; stringChar = c; i++; continue; }

            // Skip line comments
            if (c == '/' && i + 1 < query.Length && query[i + 1] == '/')
            {
                while (i < query.Length && query[i] != '\n') i++;
                continue;
            }

            // Extract identifiers
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < query.Length && (char.IsLetterOrDigit(query[i]) || query[i] == '_'))
                    i++;
                var word = query[start..i];
                if (tableSet.TryGetValue(word, out var canonical))
                    found.Add(canonical);
                continue;
            }

            i++;
        }

        return found;
    }
}
