namespace Console.Tui;

/// <summary>Shared fuzzy/subsequence matching helpers used by KQL and JMESPath autocomplete.</summary>
internal static class FuzzyMatch
{
    /// <summary>
    /// Returns true if every character of <paramref name="pattern"/> appears in
    /// <paramref name="text"/> in order (case-insensitive).
    /// </summary>
    public static bool IsSubsequenceMatch(string text, string pattern)
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
    public static int[] ComputeMatchIndices(string text, string prefix)
    {
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return [.. Enumerable.Range(0, prefix.Length)];
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
}
