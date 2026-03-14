using System.Text.Json.Nodes;
using SpecGenerator.Config;

namespace SpecGenerator.Parsing;

/// <summary>
/// Loads and parses Swagger 2.0 JSON spec files for a given service configuration.
/// </summary>
public sealed class SpecLoader
{
    private readonly string _specsRoot;
    private readonly RefResolver _resolver;

    public SpecLoader(string specsRoot)
    {
        _specsRoot = specsRoot;
        _resolver = new RefResolver(specsRoot);
    }

    public RefResolver Resolver => _resolver;

    /// <summary>
    /// Loads all spec files declared in the service config and returns them
    /// as a list of parsed <see cref="SpecDocument"/> instances.
    /// </summary>
    public List<SpecDocument> Load(ServiceConfig service)
    {
        var docs = new List<SpecDocument>();

        foreach (var specFile in service.SpecFiles)
        {
            var fullPath = Path.IsPathRooted(specFile)
                ? specFile
                : Path.GetFullPath(Path.Combine(_specsRoot, specFile));

            if (!File.Exists(fullPath))
            {
                Console.Error.WriteLine($"Warning: spec file not found: {fullPath}");
                continue;
            }

            var doc = ParseFile(fullPath);
            if (doc is not null)
            {
                _resolver.Register(doc);
                docs.Add(doc);
            }
        }

        return docs;
    }

    private static SpecDocument? ParseFile(string filePath)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var root = JsonNode.Parse(text)?.AsObject();
            if (root is null)
            {
                Console.Error.WriteLine($"Warning: could not parse JSON from {filePath}");
                return null;
            }

            return new SpecDocument(filePath, root);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to read {filePath}: {ex.Message}");
            return null;
        }
    }
}
