using System.CommandLine;
using System.Reflection;

namespace Console.Cli;

public sealed class CliCompletionContext
{
    public string WordToComplete { get; }
    private readonly RootCommandDef _root;

    internal CliCompletionContext(string wordToComplete, RootCommandDef root)
    {
        WordToComplete = wordToComplete;
        _root = root;
    }

    /// <summary>
    /// Returns the first option pack of type <typeparamref name="T"/> found in the command tree,
    /// pre-populated with values parsed from the current command line.
    /// </summary>
    public T? GetOptionPack<T>() where T : OptionPack =>
        FindPack<T>(_root, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static T? FindPack<T>(object obj, HashSet<object> visited) where T : OptionPack
    {
        if (!visited.Add(obj)) return null;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                var value = field.GetValue(obj);
                if (value is T found) return found;
                if (value is OptionPack nested)
                {
                    var fromPack = FindPack<T>(nested, visited);
                    if (fromPack != null) return fromPack;
                }
            }
        }
        return null;
    }
}

public interface ICliCompletionProvider
{
    ValueTask<IEnumerable<string>> GetCompletionsAsync(CliCompletionContext context);
}

internal static class CliCompletionProviderRegistry
{
    private static readonly Dictionary<string, Func<CliCompletionContext, ValueTask<IEnumerable<string>>>> _providers = new();

    internal static void Register(string[] aliases, Type providerType)
    {
        var provider = (ICliCompletionProvider)Activator.CreateInstance(providerType)!;
        Func<CliCompletionContext, ValueTask<IEnumerable<string>>> fn = ctx => provider.GetCompletionsAsync(ctx);
        foreach (var alias in aliases)
            _providers[alias] = fn;
    }

    internal static void Register(string[] aliases, string[] values)
    {
        Func<CliCompletionContext, ValueTask<IEnumerable<string>>> fn = ctx =>
            ValueTask.FromResult(values.Where(v => v.StartsWith(ctx.WordToComplete, StringComparison.OrdinalIgnoreCase)));
        foreach (var alias in aliases)
            _providers[alias] = fn;
    }

    internal static Func<CliCompletionContext, ValueTask<IEnumerable<string>>>? Resolve(string alias) =>
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

        // Parse the command line (excluding the incomplete word) to populate option packs
        var parseArgs = (trailingSpace ? tokens.Skip(1) : tokens.Skip(1).SkipLast(1)).ToArray();
        var parseResult = rootCmd.Parse(parseArgs, new CommandLineConfiguration(rootCmd));
        InjectParseResult(root, parseResult);

        var activeCmd = FindActiveCommand(rootCmd, tokens, trailingSpace);
        var context = new CliCompletionContext(wordToComplete, root);

        // Complete an option's value
        if (precedingToken?.StartsWith('-') == true)
        {
            var resolve = CliCompletionProviderRegistry.Resolve(precedingToken);
            if (resolve != null)
            {
                foreach (var c in await resolve(context))
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

    private static void InjectParseResult(object obj, ParseResult result)
    {
        InjectParseResult(obj, result, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static void InjectParseResult(object obj, ParseResult result, HashSet<object> visited)
    {
        if (!visited.Add(obj)) return;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                if (field.GetValue(obj) is OptionPack pack)
                {
                    pack.SetParseResult(result);
                    InjectParseResult(pack, result, visited);
                }
            }
        }
    }

    private static Command FindActiveCommand(Command root, List<string> tokens, bool trailingSpace)
    {
        var current = root;
        for (int i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!trailingSpace && i == tokens.Count - 1) break;
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
