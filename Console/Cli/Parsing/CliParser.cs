namespace Console.Cli.Parsing;

/// <summary>
/// Hand-written CLI parser that walks the CommandDef hierarchy.
/// Replaces System.CommandLine's parsing pipeline.
/// </summary>
internal static class CliParser
{
    /// <summary>
    /// Parse command-line arguments against a CommandDef tree.
    /// </summary>
    public static CliParseResult Parse(string[] args, CommandDef root)
    {
        var result = new CliParseResult { RawArgs = args };
        var tokens = Tokenize(args);

        // Phase 1: Extract directives (bracketed tokens at the start)
        int tokenStart = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var directive = CliDirective.TryParse(tokens[i].Raw);
            if (directive is not null)
            {
                result.Directives.Add(directive);
                tokenStart = i + 1;
            }
            else
            {
                break;
            }
        }

        // Phase 2: Walk command tree to find the leaf command
        var commandPath = new List<CommandDef> { root };
        int i2 = tokenStart;

        while (i2 < tokens.Count)
        {
            var token = tokens[i2];

            if (token.Raw == "--")
            {
                i2++;
                break;
            }

            // Skip option tokens during command resolution
            if (token.Raw.StartsWith('-'))
            {
                i2++;
                // Skip the value token for non-bool options
                if (i2 < tokens.Count && !tokens[i2].Raw.StartsWith('-') && !token.Raw.Contains('='))
                {
                    // Peek: is this an option that takes a value?
                    var currentCmd = commandPath[^1];
                    var opt = FindOption(currentCmd, commandPath, token.Raw);
                    if (opt is not null && !opt.IsBool && !opt.ValueIsOptional)
                        i2++; // consume the value
                }
                continue;
            }

            // Try to match as subcommand
            var parent = commandPath[^1];
            CommandDef? matched = null;
            foreach (var child in parent.EnumerateChildren())
            {
                if (string.Equals(child.Name, token.Raw, StringComparison.OrdinalIgnoreCase))
                {
                    matched = child;
                    break;
                }
                foreach (var alias in child.Aliases)
                {
                    if (string.Equals(alias, token.Raw, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = child;
                        break;
                    }
                }
                if (matched is not null)
                    break;
            }

            if (matched is not null)
            {
                commandPath.Add(matched);
                i2++;
            }
            else
            {
                // Not a subcommand — could be a positional argument or unknown token
                break;
            }
        }

        var leafCmd = commandPath[^1];

        // Phase 3: Build option map from leaf command (including recursive from ancestors)
        var allOptions = CollectAllOptions(leafCmd, commandPath);
        var optionMap = BuildOptionMap(allOptions);

        // Phase 3b: Initialize collection options so TryParseMany can accumulate into them
        foreach (var opt in allOptions)
        {
            if (opt.AllowMultipleArgumentsPerToken)
                opt.ApplyDefault();
        }

        // Phase 4: Parse option values and collect remaining tokens
        var positionalArgs = new List<string>();
        var arguments = leafCmd.EnumerateArguments().ToList();
        bool phase4AfterDoubleDash = false;

        for (int j = tokenStart; j < tokens.Count; j++)
        {
            var token = tokens[j];

            if (phase4AfterDoubleDash)
            {
                // After --, everything is treated as positional
                positionalArgs.Add(token.Raw);
                continue;
            }

            if (token.Raw == "--")
            {
                phase4AfterDoubleDash = true;
                continue;
            }

            // Check if this token was consumed during command resolution
            var isCommand = false;
            foreach (var cmd in commandPath.Skip(1))
            {
                if (string.Equals(cmd.Name, token.Raw, StringComparison.OrdinalIgnoreCase)
                    || cmd.Aliases.Any(a => string.Equals(a, token.Raw, StringComparison.OrdinalIgnoreCase)))
                {
                    isCommand = true;
                    break;
                }
            }
            if (isCommand)
                continue;

            // Directive tokens already consumed
            if (CliDirective.TryParse(token.Raw) is not null && j < tokenStart)
                continue;

            // Option: --foo=bar
            if (token.Raw.Contains('=') && token.Raw.StartsWith('-'))
            {
                var eqIdx = token.Raw.IndexOf('=');
                var optName = token.Raw[..eqIdx];
                var optValue = token.Raw[(eqIdx + 1)..];
                if (optionMap.TryGetValue(optName, out var opt))
                {
                    if (!opt.TryParse(optValue))
                        result.Errors.Add($"Cannot parse '{optValue}' for option '{optName}'.");
                }
                else
                {
                    result.UnmatchedTokens.Add(token.Raw);
                }
                continue;
            }

            // Option: --foo value or --flag
            if (token.Raw.StartsWith('-'))
            {
                var optName = token.Raw;

                // Handle --no-X boolean negation
                if (optName.StartsWith("--no-") && !optionMap.ContainsKey(optName))
                {
                    var positive = "--" + optName[5..];
                    if (optionMap.TryGetValue(positive, out var boolOpt) && boolOpt.IsBool)
                    {
                        boolOpt.TryParse("false");
                        continue;
                    }
                }

                if (optionMap.TryGetValue(optName, out var matchedOpt))
                {
                    if (matchedOpt.IsBool)
                    {
                        // Bool options: check if next token is true/false, otherwise flag-style
                        if (j + 1 < tokens.Count
                            && !tokens[j + 1].Raw.StartsWith('-')
                            && (tokens[j + 1].Raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                                || tokens[j + 1].Raw.Equals("false", StringComparison.OrdinalIgnoreCase)))
                        {
                            j++;
                            matchedOpt.TryParse(tokens[j].Raw);
                        }
                        else
                        {
                            // Determine truth value from the alias used
                            var isNegation = optName.StartsWith("--no-", StringComparison.OrdinalIgnoreCase);
                            matchedOpt.TryParse(isNegation ? "false" : null);
                        }
                    }
                    else if (matchedOpt.AllowMultipleArgumentsPerToken)
                    {
                        // Consume all following non-option tokens as values
                        var values = new List<string>();
                        while (j + 1 < tokens.Count && !tokens[j + 1].Raw.StartsWith('-'))
                        {
                            j++;
                            values.Add(tokens[j].Raw);
                        }
                        if (values.Count > 0)
                            matchedOpt.TryParseMany(values);
                        else
                            result.Errors.Add($"Option '{optName}' requires a value.");
                    }
                    else
                    {
                        // Consume next token as value
                        if (j + 1 < tokens.Count && !tokens[j + 1].Raw.StartsWith('-'))
                        {
                            j++;
                            if (!matchedOpt.TryParse(tokens[j].Raw))
                                result.Errors.Add($"Cannot parse '{tokens[j].Raw}' for option '{optName}'.");
                        }
                        else if (matchedOpt.ValueIsOptional)
                        {
                            // Nullable types and options with defaults accept bare flags
                            matchedOpt.TryParse(null);
                        }
                        else
                        {
                            result.Errors.Add($"Option '{optName}' requires a value.");
                        }
                    }
                }
                else if (TryParseStackedAlias(optName, optionMap))
                {
                    // Handled by TryParseStackedAlias
                }
                else
                {
                    result.UnmatchedTokens.Add(token.Raw);
                }
                continue;
            }

            // Non-option, non-command token → positional argument
            positionalArgs.Add(token.Raw);
        }

        // Phase 5: Assign positional arguments
        for (int k = 0; k < positionalArgs.Count && k < arguments.Count; k++)
            arguments[k].TryParse(positionalArgs[k]);

        // Remaining positional args are unmatched
        for (int k = arguments.Count; k < positionalArgs.Count; k++)
        {
            // Don't mark command path tokens as unmatched
            if (!commandPath.Skip(1).Any(c =>
                    string.Equals(c.Name, positionalArgs[k], StringComparison.OrdinalIgnoreCase)
                    || c.Aliases.Any(a => string.Equals(a, positionalArgs[k], StringComparison.OrdinalIgnoreCase))))
            {
                result.UnmatchedTokens.Add(positionalArgs[k]);
            }
        }

        // Phase 6: Apply defaults for unprovided options
        foreach (var opt in allOptions)
            opt.ApplyDefault();

        // Phase 7: Check required options
        foreach (var opt in allOptions)
        {
            if (opt.Required && !opt.WasProvided)
                result.Errors.Add($"Option '{opt.Name}' is required.");
        }

        result.Command = leafCmd;
        result.CommandPath = commandPath;
        return result;
    }

    /// <summary>
    /// Collect all options available to a command, including recursive options from ancestors.
    /// </summary>
    private static List<CliOption> CollectAllOptions(CommandDef leaf, List<CommandDef> commandPath)
    {
        var result = new List<CliOption>();
        var seen = new HashSet<string>();

        // Options directly on the leaf command
        foreach (var opt in leaf.EnumerateAllOptions())
        {
            if (seen.Add(opt.Name))
                result.Add(opt);
        }

        // Recursive options from ancestors
        for (int i = commandPath.Count - 2; i >= 0; i--)
        {
            foreach (var opt in commandPath[i].EnumerateAllOptions())
            {
                if (opt.Recursive && seen.Add(opt.Name))
                    result.Add(opt);
            }
        }

        return result;
    }

    /// <summary>
    /// Try to match a token like "-vvv" as a stacked short alias.
    /// Returns true if it matched and set the value, false otherwise.
    /// </summary>
    private static bool TryParseStackedAlias(string token, Dictionary<string, CliOption> optionMap)
    {
        // Must be a single-dash token with 2+ repeated chars: -vv, -vvv, etc.
        if (token.Length < 3 || token[0] != '-' || token[1] == '-')
            return false;

        var ch = token[1];
        for (int i = 2; i < token.Length; i++)
        {
            if (token[i] != ch)
                return false;
        }

        // Look up the single-char alias
        var alias = $"-{ch}";
        if (!optionMap.TryGetValue(alias, out var opt) || !opt.Stackable)
            return false;

        var count = token.Length - 1; // number of repeated chars
        opt.TryParse(count.ToString());
        return true;
    }

    /// <summary>
    /// Build a map from all option names/aliases to their CliOption instance.
    /// Also validates that Stackable is only used on int options.
    /// </summary>
    private static Dictionary<string, CliOption> BuildOptionMap(List<CliOption> options)
    {
        var map = new Dictionary<string, CliOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var opt in options)
        {
            if (opt.Stackable && !opt.IsInt)
                throw new InvalidOperationException(
                    $"Option '{opt.Name}' is marked Stackable but its type is {opt.ValueTypeName}. " +
                    "Stackable is only supported on int options.");

            foreach (var name in opt.AllNames)
            {
                map.TryAdd(name, opt);
            }
        }
        return map;
    }

    /// <summary>
    /// Find a specific option by name/alias in the current command context.
    /// Used during command resolution phase to skip option values.
    /// </summary>
    private static CliOption? FindOption(CommandDef cmd, List<CommandDef> commandPath, string token)
    {
        // Handle --foo=bar
        var name = token.Contains('=') ? token[..token.IndexOf('=')] : token;

        foreach (var opt in cmd.EnumerateAllOptions())
        {
            foreach (var n in opt.AllNames)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return opt;
            }
        }

        // Check recursive options from ancestors
        for (int i = commandPath.Count - 2; i >= 0; i--)
        {
            foreach (var opt in commandPath[i].EnumerateAllOptions())
            {
                if (!opt.Recursive) continue;
                foreach (var n in opt.AllNames)
                {
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                        return opt;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tokenize arguments, handling quoted strings and --foo=bar splitting.
    /// Each token preserves the original raw string.
    /// </summary>
    internal static List<Token> Tokenize(string[] args)
    {
        var tokens = new List<Token>(args.Length);
        foreach (var arg in args)
            tokens.Add(new Token(arg));
        return tokens;
    }

    internal readonly record struct Token(string Raw);
}
