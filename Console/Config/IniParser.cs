namespace Console.Config;

/// <summary>
/// Simple hand-rolled INI parser.
/// Supports [section], key = value, ; comment, # comment, and blank lines.
/// Values are trimmed; inline comments (after ; or #) are stripped.
/// </summary>
internal static class IniParser
{
    /// <summary>Parses INI text and returns section → key → raw value.</summary>
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentName = "";
        result[currentName] = currentSection;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();

            // Skip blank lines and full-line comments
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;

            // Section header: [section name]
            if (line[0] == '[' && line[^1] == ']')
            {
                currentName = line[1..^1].Trim();
                if (!result.TryGetValue(currentName, out currentSection!))
                {
                    currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[currentName] = currentSection;
                }
                continue;
            }

            // Key = value
            var eqIdx = line.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            var key = line[..eqIdx].Trim();
            var rawValue = line[(eqIdx + 1)..];

            // Strip inline comment (the first ; or # not inside a quoted token)
            var commentIdx = FindInlineComment(rawValue);
            if (commentIdx >= 0)
                rawValue = rawValue[..commentIdx];

            currentSection[key] = rawValue.Trim();
        }

        return result;
    }

    private static int FindInlineComment(string value)
    {
        // Walk the value looking for an unquoted ; or #
        // This simple implementation does not handle quoted strings, which is fine for our INI format
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is ';' or '#')
                return i;
        }
        return -1;
    }
}
