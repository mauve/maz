namespace Console.Tui;

/// <summary>Formats a KQL query: each pipe segment on its own indented line.</summary>
internal static class KqlFormatter
{
    public static string Format(string query)
    {
        var segments = SplitOnPipes(query);
        var result = new List<string>();
        foreach (var seg in segments)
        {
            var trimmed = seg.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            // Defensive: strip any leading pipe that got into the segment
            if (trimmed.StartsWith('|'))
                trimmed = trimmed[1..].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            result.Add(result.Count == 0 ? trimmed : "| " + trimmed);
        }
        return string.Join("\n", result);
    }

    private static List<string> SplitOnPipes(string query)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inString = false;
        char stringChar = '"';
        bool inComment = false;

        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];

            if (inComment)
            {
                if (c == '\n') inComment = false;
                current.Append(c);
                continue;
            }

            if (inString)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < query.Length)
                    current.Append(query[++i]);
                else if (c == stringChar)
                    inString = false;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                stringChar = c;
                current.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < query.Length && query[i + 1] == '/')
            {
                inComment = true;
                current.Append(c);
                continue;
            }

            if (c == '|')
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            segments.Add(current.ToString());

        return segments;
    }
}
