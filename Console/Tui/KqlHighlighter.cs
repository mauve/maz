using Console.Rendering;

namespace Console.Tui;

/// <summary>Applies ANSI syntax highlighting to a single KQL line.</summary>
internal static class KqlHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "summarize", "by", "extend", "project", "distinct", "top", "limit", "take",
        "join", "union", "let", "as", "render", "make-series", "mv-expand", "parse", "range",
        "sort", "order", "on", "kind", "type", "search", "print", "getschema", "evaluate",
        "invoke", "datatable", "typeof", "with", "to", "of", "step", "and", "or", "not",
        "true", "false", "null", "asc", "desc", "nulls", "first", "last", "isfuzzy",
        "project-away", "project-rename", "project-reorder", "project-keep", "project-smart",
        "mv-apply",
    };

    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "sum", "avg", "min", "max", "countif", "sumif", "dcount", "dcountif",
        "any", "stdev", "variance", "percentile", "tdigest", "make_list", "make_set",
        "bag_pack", "pack", "pack_all", "tostring", "toint", "tolong", "todouble",
        "todatetime", "totimespan", "format_datetime", "format_timespan", "startofday",
        "endofday", "startofmonth", "endofmonth", "startofyear", "endofyear",
        "datetime_add", "datetime_diff", "now", "ago", "bin", "floor", "ceiling", "round",
        "abs", "log", "log2", "log10", "exp", "pow", "sqrt", "strlen", "substring",
        "indexof", "split", "strcat", "toupper", "tolower", "trim", "replace", "extract",
        "parse_json", "todynamic", "tobool", "iff", "iif", "case", "coalesce",
        "isempty", "isnotempty", "isnull", "isnotnull", "strcat_array", "array_length",
        "array_slice", "set_union", "set_intersect", "set_difference", "bag_keys",
        "series_stats", "zip", "make_timespan", "make_datetime",
    };

    /// <summary>Returns the line with ANSI color codes injected for KQL syntax elements.</summary>
    public static string Highlight(string line)
    {
        if (!Ansi.IsEnabled)
            return line;

        // Full-line comment
        if (line.TrimStart().StartsWith("//"))
            return Ansi.Dim(line);

        var sb = new System.Text.StringBuilder();
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];

            // String literal (double or single quote)
            if (c == '"' || c == '\'')
            {
                char quote = c;
                int start = i++;
                while (i < line.Length && line[i] != quote)
                {
                    if (line[i] == '\\') i++; // escape sequence
                    i++;
                }
                if (i < line.Length) i++; // closing quote
                sb.Append(Ansi.Green(line[start..i]));
                continue;
            }

            // Inline comment
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                sb.Append(Ansi.Dim(line[i..]));
                break;
            }

            // Pipe operator
            if (c == '|')
            {
                sb.Append(Ansi.Bold("|"));
                i++;
                continue;
            }

            // Number (integer, float, with optional timespan unit suffix d/h/m/s)
            if (char.IsDigit(c))
            {
                int start = i;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.'))
                    i++;
                if (i < line.Length && (line[i] == 'e' || line[i] == 'E'))
                {
                    i++;
                    if (i < line.Length && (line[i] == '+' || line[i] == '-')) i++;
                    while (i < line.Length && char.IsDigit(line[i])) i++;
                }
                if (i < line.Length && "dhms".Contains(line[i]))
                    i++;
                sb.Append(Ansi.Magenta(line[start..i]));
                continue;
            }

            // Identifier / keyword / function name
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '-'))
                    i++;
                var word = line[start..i];
                if (Keywords.Contains(word))
                    sb.Append(Ansi.Cyan(word));
                else if (Functions.Contains(word) && i < line.Length && line[i] == '(')
                    sb.Append(Ansi.Yellow(word));
                else
                    sb.Append(word);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
