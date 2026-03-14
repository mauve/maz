using System.Text.Json.Nodes;

namespace SpecGenerator.Parsing;

/// <summary>
/// Resolves JSON $ref references in a Swagger 2.0 document,
/// supporting same-file (#/...) and relative cross-file (../path#/...) refs.
/// </summary>
public sealed class RefResolver
{
    private readonly string _baseDir;
    private readonly Dictionary<string, SpecDocument> _docCache = [];

    public RefResolver(string baseDir)
    {
        _baseDir = baseDir;
    }

    public void Register(SpecDocument doc)
    {
        _docCache[doc.FilePath] = doc;
    }

    /// <summary>
    /// Resolves a $ref string against the given context document.
    /// Returns the resolved JsonObject, or null if resolution fails.
    /// </summary>
    public JsonObject? Resolve(string refValue, SpecDocument contextDoc)
    {
        if (string.IsNullOrWhiteSpace(refValue))
            return null;

        string docPath;
        string jsonPointer;

        var hashIdx = refValue.IndexOf('#', StringComparison.Ordinal);
        if (hashIdx < 0)
        {
            // File ref with no pointer — resolve whole document root
            docPath = ResolveFilePath(refValue, contextDoc.FilePath);
            jsonPointer = string.Empty;
        }
        else if (hashIdx == 0)
        {
            // Same-file ref: #/definitions/Foo
            docPath = contextDoc.FilePath;
            jsonPointer = refValue[1..]; // strip leading #
        }
        else
        {
            // Cross-file ref: ../path/to/file.json#/parameters/Bar
            var filePart = refValue[..hashIdx];
            docPath = ResolveFilePath(filePart, contextDoc.FilePath);
            jsonPointer = refValue[(hashIdx + 1)..];
        }

        var doc = LoadDoc(docPath);
        if (doc is null)
            return null;

        return TraversePointer(doc.Root, jsonPointer);
    }

    private string ResolveFilePath(string relativePath, string contextFilePath)
    {
        var dir = Path.GetDirectoryName(contextFilePath) ?? _baseDir;
        return Path.GetFullPath(Path.Combine(dir, relativePath));
    }

    private SpecDocument? LoadDoc(string filePath)
    {
        if (_docCache.TryGetValue(filePath, out var cached))
            return cached;

        if (!File.Exists(filePath))
            return null;

        try
        {
            var text = File.ReadAllText(filePath);
            var root = JsonNode.Parse(text)?.AsObject();
            if (root is null)
                return null;

            var doc = new SpecDocument(filePath, root);
            _docCache[filePath] = doc;
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? TraversePointer(JsonObject root, string jsonPointer)
    {
        if (string.IsNullOrEmpty(jsonPointer) || jsonPointer == "/")
            return root;

        // JSON Pointer segments are separated by /
        var segments = jsonPointer.TrimStart('/').Split('/');
        JsonNode? current = root;

        foreach (var segment in segments)
        {
            // Unescape JSON Pointer tokens (~1 → /, ~0 → ~)
            var key = segment.Replace("~1", "/").Replace("~0", "~");
            current = current?[key];
            if (current is null)
                return null;
        }

        return current?.AsObject();
    }
}
