using System.Collections;

namespace Console.Cli.Parsing;

/// <summary>
/// Lightweight CLI option type with metadata built-in.
/// Replaces System.CommandLine.Option and eliminates the need for external registries.
/// </summary>
public abstract class CliOption
{
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    public string? Description { get; init; }
    public bool Required { get; init; }
    public bool Hidden { get; init; }
    public bool Recursive { get; init; }
    public bool IsAdvanced { get; init; }
    public bool AllowMultipleArgumentsPerToken { get; init; }
    public OptionGroupInfo? HelpGroup { get; set; }
    public OptionMetadata? Metadata { get; init; }

    /// <summary>True when the parser matched this option in the input.</summary>
    public bool WasProvided { get; set; }

    /// <summary>All names/aliases by which this option can be referenced on the command line.</summary>
    public IEnumerable<string> AllNames
    {
        get
        {
            yield return Name;
            foreach (var alias in Aliases)
                yield return alias;
        }
    }

    /// <summary>Try to parse a raw string value into this option's typed storage.</summary>
    public abstract bool TryParse(string? raw);

    /// <summary>Try to parse multiple raw values (for multi-value options).</summary>
    public abstract bool TryParseMany(List<string> rawValues);

    /// <summary>Reset this option to its default state.</summary>
    public abstract void Reset();

    /// <summary>Apply the default value (if any) when the option was not provided.</summary>
    public abstract void ApplyDefault();

    /// <summary>Returns true if this is a boolean option (supports --no-X negation).</summary>
    public abstract bool IsBool { get; }

    /// <summary>The underlying value type display name (for error messages).</summary>
    public abstract string ValueTypeName { get; }
}

public sealed class CliOption<T> : CliOption
{
    public T? Value { get; set; }
    public T? DefaultValue { get; init; }

    /// <summary>
    /// Parser for single-value options: takes a raw string, returns the typed value.
    /// For collection options, this is an element parser that returns a single element
    /// which then gets added to the collection.
    /// </summary>
    public Func<string, T>? Parser { get; init; }

    /// <summary>
    /// Element parser for collection options (List&lt;E&gt;).
    /// Takes a raw string, returns a single element of the collection's element type.
    /// Used by TryParseMany to accumulate elements.
    /// </summary>
    public Func<string, object>? ElementParser { get; init; }

    public Func<T>? DefaultValueFactory { get; init; }

    public override bool IsBool => typeof(T) == typeof(bool);
    public override string ValueTypeName => typeof(T).Name;

    public override bool TryParse(string? raw)
    {
        if (raw is null)
        {
            if (typeof(T) == typeof(bool))
            {
                Value = (T)(object)true;
                WasProvided = true;
                return true;
            }
            return false;
        }

        try
        {
            // For collection types with ElementParser, add a single element
            if (ElementParser is not null && Value is IList list)
            {
                list.Add(ElementParser(raw));
                WasProvided = true;
                return true;
            }

            if (Parser is not null)
            {
                Value = Parser(raw);
                WasProvided = true;
                return true;
            }

            var (ok, parsed) = TryParseValue(raw);
            if (ok)
            {
                Value = parsed;
                WasProvided = true;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public override bool TryParseMany(List<string> rawValues)
    {
        if (rawValues.Count == 0)
            return false;

        // Collection with element parser: accumulate elements
        if (ElementParser is not null && Value is IList list)
        {
            foreach (var raw in rawValues)
                list.Add(ElementParser(raw));
            WasProvided = true;
            return true;
        }

        // List<string>: accumulate directly
        if (Value is List<string> stringList)
        {
            foreach (var raw in rawValues)
                stringList.Add(raw);
            WasProvided = true;
            return true;
        }

        // Fallback: parse the last value
        return TryParse(rawValues[^1]);
    }

    public override void Reset()
    {
        Value = DefaultValue;
        WasProvided = false;
    }

    public override void ApplyDefault()
    {
        if (WasProvided)
            return;

        if (DefaultValueFactory is not null)
        {
            Value = DefaultValueFactory();
            return;
        }

        Value = DefaultValue;
    }

    /// <summary>Returns (true, value) on success, (false, default) on failure.</summary>
    private static (bool ok, T? value) TryParseValue(string raw)
    {
        var targetType = typeof(T);
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlyingType == typeof(string))
            return (true, (T)(object)raw);

        if (underlyingType == typeof(bool))
            return bool.TryParse(raw, out var b) ? (true, (T)(object)b) : (false, default);

        if (underlyingType == typeof(int))
            return int.TryParse(raw, out var i) ? (true, (T)(object)i) : (false, default);

        if (underlyingType == typeof(long))
            return long.TryParse(raw, out var l) ? (true, (T)(object)l) : (false, default);

        if (underlyingType == typeof(double))
            return double.TryParse(raw, out var d) ? (true, (T)(object)d) : (false, default);

        if (underlyingType == typeof(Guid))
            return Guid.TryParse(raw, out var g) ? (true, (T)(object)g) : (false, default);

        if (underlyingType.IsEnum)
            return Enum.TryParse(underlyingType, raw, ignoreCase: true, out var e)
                ? (true, (T)e!) : (false, default);

        if (underlyingType == typeof(Uri))
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return (true, (T)(object)uri);
            return (false, default);
        }

        // Try static Parse(string) method
        var parseMethod = underlyingType.GetMethod("Parse", [typeof(string)]);
        if (parseMethod is { IsStatic: true })
        {
            try { return (true, (T)parseMethod.Invoke(null, [raw])!); }
            catch { return (false, default); }
        }

        return (false, default);
    }
}
