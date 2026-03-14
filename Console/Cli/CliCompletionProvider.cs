using System.CommandLine;

namespace Console.Cli;

public interface ICliCompletionProvider
{
    ValueTask<IEnumerable<string>> GetCompletionsAsync(string wordToComplete);
}

internal static class CliCompletionProviderRegistry
{
    private static readonly Dictionary<string, Func<string, ValueTask<IEnumerable<string>>>> _providers = new();

    internal static void Register(string[] aliases, Type providerType)
    {
        var provider = (ICliCompletionProvider)Activator.CreateInstance(providerType)!;
        Func<string, ValueTask<IEnumerable<string>>> fn = word => provider.GetCompletionsAsync(word);
        foreach (var alias in aliases)
            _providers[alias] = fn;
    }

    internal static void Register(string[] aliases, string[] values)
    {
        Func<string, ValueTask<IEnumerable<string>>> fn = word =>
            ValueTask.FromResult(values.Where(v => v.StartsWith(word, StringComparison.OrdinalIgnoreCase)));
        foreach (var alias in aliases)
            _providers[alias] = fn;
    }

    internal static Func<string, ValueTask<IEnumerable<string>>>? Resolve(string alias) =>
        _providers.TryGetValue(alias, out var fn) ? fn : null;
}

internal static class CliCompletionHandler
{
    internal static async Task HandleAsync(string commandLine, int cursorPosition, RootCommandDef root)
    {
        var rootCmd = root.Build(); // also populates CliCompletionProviderRegistry

        var line = cursorPosition < commandLine.Length ? commandLine[..cursorPosition] : commandLine;
        var tokens = Tokenize(line);
        bool trailingSpace = line.EndsWith(' ');

        string wordToComplete = !trailingSpace && tokens.Count > 0 ? tokens[^1] : "";
        string? precedingToken = trailingSpace
            ? (tokens.Count > 0 ? tokens[^1] : null)
            : (tokens.Count >= 2 ? tokens[^2] : null);

        var activeCmd = FindActiveCommand(rootCmd, tokens, trailingSpace);

        // Complete an option's value
        if (precedingToken?.StartsWith('-') == true)
        {
            var resolve = CliCompletionProviderRegistry.Resolve(precedingToken);
            if (resolve != null)
            {
                foreach (var c in await resolve(wordToComplete))
                    System.Console.WriteLine(c);
            }
            return;
        }

        // Complete an option name
        if (wordToComplete.StartsWith('-'))
        {
            foreach (var opt in activeCmd.Options)
            {
                if (opt.Hidden) continue;
                foreach (var alias in opt.Aliases)
                    if (alias.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                        System.Console.WriteLine(alias);
            }
            return;
        }

        // Complete a subcommand name
        foreach (var sub in activeCmd.Subcommands)
        {
            if (sub.Hidden) continue;
            if (sub.Name.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                System.Console.WriteLine(sub.Name);
        }
    }

    private static Command FindActiveCommand(Command root, List<string> tokens, bool trailingSpace)
    {
        var current = root;
        for (int i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!trailingSpace && i == tokens.Count - 1) break; // last token is wordToComplete
            if (token.StartsWith('-')) continue;
            var sub = current.Subcommands.FirstOrDefault(
                s => s.Name == token || s.Aliases.Contains(token));
            if (sub is null) continue;
            current = sub;
        }
        return current;
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
