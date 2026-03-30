using System.Text.Json;
using System.Text.Json.Serialization;

namespace Console.Cli.Commands.Browse;

/// <summary>NDJSON export entry for a blob. Used by both browse export and storage query.</summary>
internal sealed class BlobExportEntry
{
    [JsonPropertyName("account")]
    public required string Account { get; init; }

    [JsonPropertyName("container")]
    public required string Container { get; init; }

    [JsonPropertyName("blob")]
    public required string Blob { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("size")]
    public required long Size { get; init; }

    [JsonPropertyName("contentType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; init; }

    [JsonPropertyName("contentMd5")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentMd5 { get; init; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; init; }

    [JsonPropertyName("lastModified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastModified { get; init; }
}

[JsonSerializable(typeof(BlobExportEntry))]
internal partial class BlobExportJsonContext : JsonSerializerContext
{
    private static BlobExportJsonContext? _relaxed;

    public static BlobExportJsonContext RelaxedEncoding =>
        _relaxed ??= new(
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }
        );
}
