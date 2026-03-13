// ParseResult.GetValue is annotated [MaybeNull] but callers handle nullability via option defaults
#pragma warning disable CS8603
using System.CommandLine;

namespace Console.Cli;

public interface IGlobalOption { }

/// <summary>Marker subclass — infrastructure adds this as a recursive (global) option.</summary>
public class GlobalOption<T> : Option<T>, IGlobalOption
{
    public GlobalOption(string name, string? description = null)
        : base(name, [])
    {
        Description = description ?? "";
        Recursive = true;
    }

    public GlobalOption(string name, string[] aliases, string? description = null)
        : base(name, aliases)
    {
        Description = description ?? "";
        Recursive = true;
    }
}

public abstract class OptionPack
{
    private ParseResult _result = null!;

    internal void SetParseResult(ParseResult r) => _result = r;

    protected T GetValue<T>(Option<T> option) => _result.GetValue(option)!;

    internal abstract void AddOptionsTo(Command command);
}
