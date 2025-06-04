using DotMake.CommandLine;

namespace Console.Shared;

public interface ITagCommand
{
    /// <summary>
    /// Gets or sets the tags for the resource.
    /// </summary>
    [CliOption(
        Description = """
            The tags for the resource.

            Tags are key-value pairs that can be used to organize and manage resources.
            """,
        Required = false,
        AllowMultipleArgumentsPerToken = true
    )]
    List<Tag>? Tags { get; set; }
}

public record Tag(string Key, string Value)
{
    public override string ToString() => $"{Key}={Value}";

    public static Tag Parse(string keyValue)
    {
        var parts = keyValue.Split('=', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException("Tag must be in the format 'key=value'.", nameof(keyValue));
        }

        return new(parts[0], parts[1]);
    }
}

public static class ITagCommandExtensions
{
    public static IDictionary<string, string>? GetTags(this ITagCommand self) =>
        self.Tags?.ToDictionary(tag => tag.Key, tag => tag.Value);

    public static void AppendTagsTo(this ITagCommand self, IDictionary<string, string> tags) =>
        self.Tags?.ForEach(tag => tags[tag.Key] = tag.Value);
}
