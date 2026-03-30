using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Simple ANSI syntax highlighter for Azure Blob Storage tag query expressions.
/// Highlights keywords (AND, OR), quoted keys, quoted values, and operators.
/// </summary>
internal static class TagQueryHighlighter
{
    public static string Highlight(string input)
    {
        if (!Ansi.IsEnabled || string.IsNullOrEmpty(input))
            return input;

        var sb = new System.Text.StringBuilder(input.Length * 2);
        int i = 0;

        while (i < input.Length)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(input[i]))
            {
                sb.Append(input[i++]);
                continue;
            }

            // Double-quoted key: "key"
            if (input[i] == '"')
            {
                int end = input.IndexOf('"', i + 1);
                if (end < 0)
                    end = input.Length - 1;
                sb.Append("\x1b[36m"); // cyan
                sb.Append(input[i..(end + 1)]);
                sb.Append("\x1b[0m");
                i = end + 1;
                continue;
            }

            // Single-quoted value: 'value'
            if (input[i] == '\'')
            {
                int end = input.IndexOf('\'', i + 1);
                if (end < 0)
                    end = input.Length - 1;
                sb.Append("\x1b[32m"); // green
                sb.Append(input[i..(end + 1)]);
                sb.Append("\x1b[0m");
                i = end + 1;
                continue;
            }

            // Operators: =, >, <, >=, <=, <>
            if (input[i] is '=' or '>' or '<')
            {
                sb.Append("\x1b[33m"); // yellow
                sb.Append(input[i]);
                if (i + 1 < input.Length && input[i + 1] is '=' or '>')
                {
                    sb.Append(input[i + 1]);
                    i++;
                }
                sb.Append("\x1b[0m");
                i++;
                continue;
            }

            // Keywords: AND, OR
            if (
                i + 3 <= input.Length
                && input[i..(i + 3)].Equals("AND", StringComparison.OrdinalIgnoreCase)
                && (i + 3 >= input.Length || !char.IsLetterOrDigit(input[i + 3]))
            )
            {
                sb.Append("\x1b[1;34m"); // bold blue
                sb.Append(input[i..(i + 3)]);
                sb.Append("\x1b[0m");
                i += 3;
                continue;
            }

            if (
                i + 2 <= input.Length
                && input[i..(i + 2)].Equals("OR", StringComparison.OrdinalIgnoreCase)
                && (i + 2 >= input.Length || !char.IsLetterOrDigit(input[i + 2]))
            )
            {
                sb.Append("\x1b[1;34m"); // bold blue
                sb.Append(input[i..(i + 2)]);
                sb.Append("\x1b[0m");
                i += 2;
                continue;
            }

            // Default: pass through
            sb.Append(input[i++]);
        }

        return sb.ToString();
    }
}
