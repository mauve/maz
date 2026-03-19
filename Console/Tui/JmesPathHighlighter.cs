using Console.Rendering;

namespace Console.Tui;

/// <summary>Applies ANSI syntax highlighting to a single JMESPath expression line.</summary>
internal static class JmesPathHighlighter
{
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "length",
        "sort",
        "sort_by",
        "group_by",
        "contains",
        "keys",
        "values",
        "type",
        "starts_with",
        "ends_with",
        "max",
        "max_by",
        "min",
        "min_by",
        "sum",
        "avg",
        "to_string",
        "to_number",
        "to_array",
        "not_null",
        "join",
        "merge",
        "floor",
        "ceil",
        "abs",
        "reverse",
        "map",
    };

    /// <summary>Returns the line with ANSI color codes injected for JMESPath syntax elements.</summary>
    public static string Highlight(string line)
    {
        if (!Ansi.IsEnabled)
            return line;

        var sb = new System.Text.StringBuilder();
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];

            // Single-quoted string literal ('...')
            if (c == '\'')
            {
                int start = i++;
                while (i < line.Length && line[i] != '\'')
                {
                    if (line[i] == '\\')
                        i++;
                    i++;
                }
                if (i < line.Length)
                    i++; // closing quote
                sb.Append(Ansi.Green(line[start..i]));
                continue;
            }

            // Raw string literal (`...`)
            if (c == '`')
            {
                int start = i++;
                while (i < line.Length && line[i] != '`')
                {
                    if (line[i] == '\\')
                        i++;
                    i++;
                }
                if (i < line.Length)
                    i++; // closing backtick
                sb.Append(Ansi.Color(line[start..i], "\x1b[36m")); // cyan
                continue;
            }

            // Double-quoted string (JSON literal in filter expressions)
            if (c == '"')
            {
                int start = i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\')
                        i++;
                    i++;
                }
                if (i < line.Length)
                    i++;
                sb.Append(Ansi.Green(line[start..i]));
                continue;
            }

            // Number
            if (char.IsDigit(c) || (c == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                int start = i;
                if (c == '-')
                    i++;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.'))
                    i++;
                sb.Append(Ansi.Magenta(line[start..i]));
                continue;
            }

            // Operators / structural characters
            if (
                c
                is '['
                    or ']'
                    or '{'
                    or '}'
                    or '.'
                    or '|'
                    or '@'
                    or '*'
                    or '&'
                    or '?'
                    or '!'
                    or '<'
                    or '>'
                    or '='
            )
            {
                sb.Append(Ansi.Bold(c.ToString()));
                i++;
                continue;
            }

            // Identifier or function name
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                var word = line[start..i];
                if (Functions.Contains(word) && i < line.Length && line[i] == '(')
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
