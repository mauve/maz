using System.Text.Json.Nodes;

namespace SpecGenerator.Parsing;

/// <summary>
/// Wraps a parsed Swagger 2.0 JSON document and provides typed accessors
/// for paths, operations, definitions, and top-level parameters.
/// </summary>
public sealed class SpecDocument
{
    public string FilePath { get; }
    public JsonObject Root { get; }

    public SpecDocument(string filePath, JsonObject root)
    {
        FilePath = filePath;
        Root = root;
    }

    public JsonObject? GetDefinition(string name) =>
        Root["definitions"]?[name]?.AsObject();

    public JsonObject? GetTopLevelParameter(string name) =>
        Root["parameters"]?[name]?.AsObject();

    public IEnumerable<(string Path, string Method, JsonObject Operation)> GetOperations()
    {
        var paths = Root["paths"]?.AsObject();
        if (paths is null)
            yield break;

        foreach (var pathProp in paths)
        {
            var pathItem = pathProp.Value?.AsObject();
            if (pathItem is null)
                continue;

            foreach (var method in HttpMethods)
            {
                var op = pathItem[method]?.AsObject();
                if (op is not null)
                    yield return (pathProp.Key, method, op);
            }
        }
    }

    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "patch"];
}
