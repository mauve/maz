using System.CommandLine;

namespace Console.Cli.Shared;

public record Tag(string Key, string Value)
{
    public override string ToString() => $"{Key}={Value}";

    public static Tag Parse(string keyValue)
    {
        var parts = keyValue.Split('=', 2);
        if (parts.Length != 2)
            throw new ArgumentException("Tag must be in the format 'key=value'.", nameof(keyValue));
        return new(parts[0], parts[1]);
    }
}

public class TagOptionPack : OptionPack
{
    public readonly Option<List<Tag>> Tags;

    public TagOptionPack()
    {
        Tags = new Option<List<Tag>>("--tags", [])
        {
            Description = "Tags as key=value pairs for the resource.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
            CustomParser = r => r.Tokens.Select(t => Tag.Parse(t.Value)).ToList()
        };
    }

    internal override void AddOptionsTo(Command cmd) => cmd.Add(Tags);

    public void AppendTagsTo(IDictionary<string, string> tags)
    {
        foreach (var tag in GetValue(Tags) ?? [])
            tags[tag.Key] = tag.Value;
    }
}
