using System.Reflection;

namespace Console.Cli;

public sealed class CliCompletionContext
{
    public string WordToComplete { get; }
    private readonly RootCommandDef? _root;

    internal CliCompletionContext(string wordToComplete, RootCommandDef? root)
    {
        WordToComplete = wordToComplete;
        _root = root;
    }

    /// <summary>
    /// Returns the first option pack of type <typeparamref name="T"/> found in the command tree,
    /// pre-populated with values parsed from the current command line.
    /// Returns null if no root was provided.
    /// </summary>
    public T? GetOptionPack<T>()
        where T : OptionPack =>
        _root == null
            ? null
            : FindPack<T>(_root, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static T? FindPack<T>(object obj, HashSet<object> visited)
        where T : OptionPack
    {
        if (!visited.Add(obj))
            return null;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = obj.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
            {
                var value = field.GetValue(obj);
                if (value is T found)
                    return found;
                if (value is OptionPack nested)
                {
                    var fromPack = FindPack<T>(nested, visited);
                    if (fromPack != null)
                        return fromPack;
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
    private static readonly Dictionary<
        string,
        Func<CliCompletionContext, ValueTask<IEnumerable<string>>>
    > _providers = new();

    internal static void Register(string[] aliases, Type providerType)
    {
        var provider = (ICliCompletionProvider)Activator.CreateInstance(providerType)!;
        Func<CliCompletionContext, ValueTask<IEnumerable<string>>> fn = ctx =>
            provider.GetCompletionsAsync(ctx);
        foreach (var alias in aliases)
            _providers[alias] = fn;
    }

    internal static void Register(string[] aliases, string[] values)
    {
        Func<CliCompletionContext, ValueTask<IEnumerable<string>>> fn = ctx =>
            ValueTask.FromResult(
                values.Where(v =>
                    v.StartsWith(ctx.WordToComplete, StringComparison.OrdinalIgnoreCase)
                )
            );
        foreach (var alias in aliases)
            _providers[alias] = fn;
    }

    internal static Func<CliCompletionContext, ValueTask<IEnumerable<string>>>? Resolve(
        string alias
    ) => _providers.TryGetValue(alias, out var fn) ? fn : null;
}

internal static class CliCompletionHandler
{
    // Public entry point — uses the compile-time generated tree and providers.
    internal static Task HandleAsync(string commandLine, int cursorPosition) =>
        HandleAsync(
            commandLine,
            cursorPosition,
            CompletionTree.Root,
            CompletionTree.DynamicProviders,
            System.Console.Out
        );

    // Testable overload — accepts an injected tree, providers, and output writer.
    internal static async Task HandleAsync(
        string commandLine,
        int cursorPosition,
        CompletionNode root,
        IReadOnlyDictionary<string, ICliCompletionProvider> dynamicProviders,
        TextWriter output
    )
    {
        var line =
            cursorPosition < commandLine.Length ? commandLine[..cursorPosition] : commandLine;
        var tokens = Tokenize(line);
        bool trailingSpace = line.EndsWith(' ');

        string wordToComplete = !trailingSpace && tokens.Count > 0 ? tokens[^1] : "";
        string? precedingToken = trailingSpace
            ? (tokens.Count > 0 ? tokens[^1] : null)
            : (tokens.Count >= 2 ? tokens[^2] : null);

        // Dynamic value completion (e.g. --subscription-id <TAB>)
        if (precedingToken?.StartsWith('-') == true)
        {
            if (dynamicProviders.TryGetValue(precedingToken, out var provider))
            {
                var context = new CliCompletionContext(wordToComplete, null);
                foreach (var c in await provider.GetCompletionsAsync(context))
                    output.WriteLine(c);
            }
            return;
        }

        // Static path: walk the compile-time generated tree
        var node = FindActiveNode(root, tokens, trailingSpace);

        if (wordToComplete.StartsWith('-'))
        {
            foreach (var opt in node.Options)
                if (opt.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                    output.WriteLine(opt);
            return;
        }

        foreach (var child in node.Children)
            if (child.Name.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                output.WriteLine(child.Name);
    }

    private static CompletionNode FindActiveNode(
        CompletionNode root,
        List<string> tokens,
        bool trailingSpace
    )
    {
        var current = root;
        for (int i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!trailingSpace && i == tokens.Count - 1)
                break;
            if (token.StartsWith('-'))
                continue;
            var sub = Array.Find(current.Children, c => c.Name == token);
            if (sub.Name == null) // default struct = not found
                break;
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
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }
}
