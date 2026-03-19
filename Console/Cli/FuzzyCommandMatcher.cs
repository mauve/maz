namespace Console.Cli;

internal static class FuzzyCommandMatcher
{
    private const int MaxResults = 5;

    public static IReadOnlyList<(int Score, CommandDef Cmd)> FindMatches(
        CommandDef parent,
        string token
    )
    {
        return parent
            .EnumerateChildren()
            .Select(c => (Score: Score(token, c.Name), Cmd: c))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(MaxResults)
            .ToList();
    }

    public static int Score(string input, string candidate)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(candidate))
            return 0;

        // Exact match or prefix match
        if (
            string.Equals(input, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase)
        )
            return 80;

        // Substring containment
        if (candidate.Contains(input, StringComparison.OrdinalIgnoreCase))
            return 50;

        var dist = LevenshteinDistance(input.ToLowerInvariant(), candidate.ToLowerInvariant());

        if (dist == 1)
            return 40;
        if (dist == 2)
            return 20;
        if (dist == 3 && candidate.Length >= 6)
            return 10;

        return 0;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var dp = new int[a.Length + 1, b.Length + 1];

        for (var i = 0; i <= a.Length; i++)
            dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            dp[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost
                );
            }
        }

        return dp[a.Length, b.Length];
    }
}
