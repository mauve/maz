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

public partial class TagOptionPack : OptionPack
{
    /// <summary>Tags as key=value pairs for the resource.</summary>
    [CliOption("--tags")]
    public partial List<Tag> Tags { get; }

    internal override void AddOptionsTo(System.CommandLine.Command cmd)
        => AddGeneratedOptions(cmd);

    public void AppendTagsTo(IDictionary<string, string> tags)
    {
        foreach (var tag in Tags ?? [])
            tags[tag.Key] = tag.Value;
    }
}
