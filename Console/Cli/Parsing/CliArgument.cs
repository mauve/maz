namespace Console.Cli.Parsing;

/// <summary>
/// Lightweight CLI positional argument type.
/// Replaces System.CommandLine.Argument&lt;T&gt;.
/// </summary>
public class CliArgument<T>
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public T? Value { get; set; }
    public bool WasProvided { get; set; }
    public bool Hidden { get; init; }

    public bool TryParse(string raw)
    {
        var targetType = typeof(T);
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlyingType == typeof(string))
        {
            Value = (T)(object)raw;
            WasProvided = true;
            return true;
        }

        if (underlyingType == typeof(int) && int.TryParse(raw, out var i))
        {
            Value = (T)(object)i;
            WasProvided = true;
            return true;
        }

        if (underlyingType == typeof(bool) && bool.TryParse(raw, out var b))
        {
            Value = (T)(object)b;
            WasProvided = true;
            return true;
        }

        return false;
    }
}
