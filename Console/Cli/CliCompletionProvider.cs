using System.Reflection;
using Console.Cli.Shared;

namespace Console.Cli;

public sealed class CliCompletionContext
{
    public string WordToComplete { get; }
    public DiagnosticLog Log { get; }
    private readonly RootCommandDef? _root;

    internal CliCompletionContext(string wordToComplete, RootCommandDef? root, DiagnosticLog? log = null)
    {
        WordToComplete = wordToComplete;
        _root = root;
        Log = log ?? DiagnosticLog.Null;
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
    > _providers = [];

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

internal static class CliArgumentCompletionRegistry
{
    private static readonly Dictionary<string, string[][]> _registrations = [];

    internal static void Register(string commandPath, string[][] argumentCompletions) =>
        _registrations[commandPath] = argumentCompletions;

    internal static string[][]? Resolve(string commandPath) =>
        _registrations.TryGetValue(commandPath, out var values) ? values : null;
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

    // Testable overload — accepts an injected tree, providers, output writer, and optional
    // diagnostic log (used by 'maz debug suggest' to trace the completion logic).
    internal static async Task HandleAsync(
        string commandLine,
        int cursorPosition,
        CompletionNode root,
        IReadOnlyDictionary<string, ICliCompletionProvider> dynamicProviders,
        TextWriter output,
        DiagnosticLog? log = null
    )
    {
        log ??= DiagnosticLog.Null;
        var line =
            cursorPosition < commandLine.Length ? commandLine[..cursorPosition] : commandLine;
        var tokens = Tokenize(line);
        bool trailingSpace = line.EndsWith(' ');

        string wordToComplete = !trailingSpace && tokens.Count > 0 ? tokens[^1] : "";
        string? precedingToken = trailingSpace
            ? (tokens.Count > 0 ? tokens[^1] : null)
            : (tokens.Count >= 2 ? tokens[^2] : null);

        log.Trace(
            $"tokens=[{string.Join(", ", tokens.Select(t => $"\"{t}\""))}]"
        );
        log.Trace(
            $"wordToComplete=\"{wordToComplete}\" "
                + $"precedingToken={FormatNullable(precedingToken)} "
                + $"trailingSpace={trailingSpace}"
        );

        // Dynamic value completion (e.g. --subscription-id <TAB>)
        if (precedingToken?.StartsWith('-') == true)
        {
            log.Trace(
                "path: preceding token is an option → checking dynamic/static value providers"
            );

            if (dynamicProviders.TryGetValue(precedingToken, out var provider))
            {
                log.Trace(
                    $"dynamic provider found for \"{precedingToken}\" ({provider.GetType().Name})"
                );
                var context = new CliCompletionContext(wordToComplete, null, log);
                IEnumerable<string> completions;
                try
                {
                    completions = await provider.GetCompletionsAsync(context);
                }
                catch (Exception ex)
                {
                    log.Trace($"provider threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                int count = 0;
                foreach (var c in completions)
                {
                    output.WriteLine(c);
                    count++;
                }
                log.Trace($"provider returned {count} completion(s)");
                return;
            }

            // Static value completion (e.g. --format <TAB> for enum options)
            if (CompletionTree.StaticValueProviders.TryGetValue(precedingToken, out var values))
            {
                log.Trace(
                    $"static value provider found for \"{precedingToken}\" ({values.Length} value(s))"
                );
                int count = 0;
                foreach (var v in values)
                {
                    if (v.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                    {
                        output.WriteLine(v);
                        count++;
                    }
                }
                log.Trace($"emitted {count} static value(s)");
                return;
            }

            log.Trace(
                $"no provider found for option \"{precedingToken}\" — no completions"
            );
            return;
        }

        // Static path: walk the compile-time generated tree
        var (node, commandPath) = FindActiveNode(root, tokens, trailingSpace);
        log.Trace($"path: static tree, command path=\"{commandPath}\"");

        if (wordToComplete.StartsWith('-'))
        {
            log.Trace(
                $"completing option at node \"{commandPath}\" ({node.Options.Length} options available)"
            );
            int count = 0;
            foreach (var opt in node.Options)
            {
                if (opt.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                {
                    output.WriteLine(opt);
                    count++;
                }
            }
            log.Trace($"emitted {count} option(s)");
            return;
        }

        int childCount = 0;
        foreach (var child in node.Children)
        {
            if (child.Name.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine(child.Name);
                childCount++;
            }
        }
        log.Trace(
            $"node \"{commandPath}\" has {node.Children.Length} child(ren), emitted {childCount} matching \"{wordToComplete}\""
        );

        // Argument completions — only when no child commands matched
        if (node.Children.Length == 0)
        {
            var argCompletions = CliArgumentCompletionRegistry.Resolve(commandPath);
            if (argCompletions != null)
            {
                int positionalIndex = CountPositionalArgs(tokens, trailingSpace, commandPath);
                log.Trace(
                    $"positional arg completions registered for \"{commandPath}\", positionalIndex={positionalIndex}"
                );
                if (positionalIndex >= 0 && positionalIndex < argCompletions.Length)
                {
                    int count = 0;
                    foreach (var v in argCompletions[positionalIndex])
                    {
                        if (v.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
                        {
                            output.WriteLine(v);
                            count++;
                        }
                    }
                    log.Trace($"emitted {count} positional arg completion(s)");
                }
            }
            else
            {
                log.Trace(
                    $"no positional arg completions registered for \"{commandPath}\""
                );
            }
        }
    }

    private static string FormatNullable(string? value) =>
        value is null ? "(none)" : $"\"{value}\"";

    private static (CompletionNode node, string commandPath) FindActiveNode(
        CompletionNode root,
        List<string> tokens,
        bool trailingSpace
    )
    {
        var current = root;
        var pathParts = new List<string> { root.Name };
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
            pathParts.Add(sub.Name);
        }
        return (current, string.Join(' ', pathParts));
    }

    private static int CountPositionalArgs(
        List<string> tokens,
        bool trailingSpace,
        string commandPath
    )
    {
        var pathParts = commandPath.Split(' ');
        int pathDepth = pathParts.Length;
        int positional = 0;

        // Tokens after the command path that are not options are positional args
        for (int i = pathDepth; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!trailingSpace && i == tokens.Count - 1)
                break; // this is the word being completed
            if (token.StartsWith('-'))
                continue;
            positional++;
        }
        return positional;
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
